using System.Threading.Channels;
using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;
using Microsoft.Windows.Devices.Midi2.Initialization;
using Microsoft.Windows.Devices.Midi2.Messages;

namespace OpenControl.MidiHelper;

/// <summary>
/// Manages a Windows MIDI Services Virtual Device.
/// 
/// Architecture:
/// - Creates a Virtual Device visible to DAWs as "Open Control"
/// - Connects to the PRIVATE device endpoint (not visible to DAWs)
/// - Receives MIDI from DAWs via the public client endpoint
/// - Sends MIDI to DAWs via the connection
/// 
/// This architecture prevents feedback loops (like a real hardware device).
/// </summary>
public sealed class VirtualDevice : IDisposable
{
    private MidiDesktopAppSdkInitializer? _initializer;
    private MidiSession? _session;
    private MidiVirtualDevice? _device;
    private MidiEndpointConnection? _connection;
    private bool _disposed;

    // Channel for incoming MIDI messages (from DAWs)
    private readonly Channel<byte[]> _incomingMessages = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

    /// <summary>
    /// Reader for incoming MIDI messages from DAWs.
    /// </summary>
    public ChannelReader<byte[]> IncomingMessages => _incomingMessages.Reader;

    /// <summary>
    /// Gets whether the device was successfully initialized.
    /// </summary>
    public bool IsInitialized => _connection != null;

    /// <summary>
    /// Initialize the Virtual Device with Windows MIDI Services.
    /// </summary>
    /// <param name="deviceName">Display name for the device (e.g., "Open Control")</param>
    /// <param name="productId">Unique product instance ID (max 32 chars)</param>
    /// <returns>True if initialization succeeded</returns>
    public bool Initialize(string deviceName, string productId)
    {
        try
        {
            // Step 1: Initialize SDK runtime (required for desktop apps)
            Console.WriteLine("[VirtualDevice] Initializing SDK runtime...");
            _initializer = MidiDesktopAppSdkInitializer.Create();
            
            if (!_initializer.InitializeSdkRuntime())
            {
                Console.Error.WriteLine("[VirtualDevice] Failed to initialize SDK runtime");
                Console.Error.WriteLine("[VirtualDevice] Make sure Windows MIDI Services is installed");
                return false;
            }

            if (!_initializer.EnsureServiceAvailable())
            {
                Console.Error.WriteLine("[VirtualDevice] MIDI service not available");
                Console.Error.WriteLine("[VirtualDevice] Try: midi service status");
                return false;
            }
            Console.WriteLine("[VirtualDevice] SDK runtime initialized");

            // Step 2: Create device configuration
            Console.WriteLine($"[VirtualDevice] Creating device config: {deviceName}");
            var config = CreateDeviceConfig(deviceName, productId);

            // Step 3: Create MIDI session
            _session = MidiSession.Create(deviceName);
            if (_session == null)
            {
                Console.Error.WriteLine("[VirtualDevice] Failed to create MIDI session");
                return false;
            }
            Console.WriteLine("[VirtualDevice] Session created");

            // Step 4: Create virtual device
            _device = MidiVirtualDeviceManager.CreateVirtualDevice(config);
            if (_device == null)
            {
                Console.Error.WriteLine("[VirtualDevice] Failed to create virtual device");
                Console.Error.WriteLine("[VirtualDevice] Check if Virtual Device transport is available");
                return false;
            }
            Console.WriteLine("[VirtualDevice] Virtual device created");

            // Step 5: Connect to device endpoint (PRIVATE - not visible to DAWs)
            var deviceEndpointId = _device.DeviceEndpointDeviceId;
            Console.WriteLine($"[VirtualDevice] Connecting to device endpoint: {deviceEndpointId}");
            
            _connection = _session.CreateEndpointConnection(deviceEndpointId);
            if (_connection == null)
            {
                Console.Error.WriteLine("[VirtualDevice] Failed to create endpoint connection");
                return false;
            }

            // Step 6: Add device as message processing plugin (REQUIRED)
            _connection.AddMessageProcessingPlugin(_device);

            // Step 7: Wire up message received handler
            _connection.MessageReceived += OnMessageReceived;

            // Step 8: Open connection (device becomes visible to other apps)
            if (!_connection.Open())
            {
                Console.Error.WriteLine("[VirtualDevice] Failed to open connection");
                return false;
            }

            Console.WriteLine($"[VirtualDevice] Device '{deviceName}' is now active and visible to DAWs");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualDevice] Initialization error: {ex.Message}");
            Console.Error.WriteLine($"[VirtualDevice] Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Create the device configuration with endpoint info and function blocks.
    /// </summary>
    private static MidiVirtualDeviceCreationConfig CreateDeviceConfig(string name, string productId)
    {
        // Declared endpoint info (for MIDI 2.0 endpoint discovery)
        var endpointInfo = new MidiDeclaredEndpointInfo
        {
            Name = name,
            ProductInstanceId = productId,
            SpecificationVersionMajor = 1,
            SpecificationVersionMinor = 1,
            SupportsMidi10Protocol = true,
            SupportsMidi20Protocol = false, // MIDI 1.0 is sufficient for our use case
            SupportsReceivingJitterReductionTimestamps = false,
            SupportsSendingJitterReductionTimestamps = false,
            HasStaticFunctionBlocks = true,
            DeclaredFunctionBlockCount = 1
        };

        // Device identity (for SysEx device inquiry)
        var deviceIdentity = new MidiDeclaredDeviceIdentity
        {
            // Use default values - not critical for our use case
        };

        // User-supplied info (highest priority for display name)
        var userInfo = new MidiEndpointUserSuppliedInfo
        {
            Name = name,
            Description = "Open Control Bridge - Virtual MIDI Device"
        };

        // Create main configuration
        var config = new MidiVirtualDeviceCreationConfig(
            name,                                    // Transport-supplied name
            "Virtual MIDI device for Open Control",  // Description
            "Open Control",                          // Manufacturer
            endpointInfo,
            deviceIdentity,
            userInfo
        );

        // Add function block (at least one is required)
        var functionBlock = new MidiFunctionBlock
        {
            Number = 0,
            Name = "MIDI I/O",
            IsActive = true,
            UIHint = MidiFunctionBlockUIHint.Sender,
            FirstGroup = new MidiGroup(0),
            GroupCount = 1,
            Direction = MidiFunctionBlockDirection.Bidirectional,
            RepresentsMidi10Connection = MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthUnrestricted,
            MaxSystemExclusive8Streams = 0,
            MidiCIMessageVersionFormat = 0
        };
        config.FunctionBlocks.Add(functionBlock);

        return config;
    }

    /// <summary>
    /// Handle incoming MIDI messages from DAWs.
    /// </summary>
    private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        try
        {
            // Convert UMP to MIDI 1.0 bytes
            var midi1Bytes = UmpToMidi1(args);
            
            if (midi1Bytes != null && midi1Bytes.Length > 0)
            {
                // Write to channel (non-blocking)
                if (!_incomingMessages.Writer.TryWrite(midi1Bytes))
                {
                    Console.Error.WriteLine("[VirtualDevice] Warning: Incoming message queue full, dropping message");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualDevice] Error processing incoming message: {ex.Message}");
        }
    }

    /// <summary>
    /// Send MIDI 1.0 data to connected DAWs.
    /// </summary>
    /// <param name="midi1Data">Raw MIDI 1.0 bytes (status + data)</param>
    public void SendMidi(byte[] midi1Data)
    {
        if (_connection == null || midi1Data.Length == 0)
            return;

        try
        {
            // Convert MIDI 1.0 to UMP
            var ump = Midi1ToUmp(midi1Data);
            
            if (ump.HasValue)
            {
                // Send with timestamp 0 (immediate)
                var result = _connection.SendSingleMessageWords(0, ump.Value);
                
                if (!MidiEndpointConnection.SendMessageSucceeded(result))
                {
                    Console.Error.WriteLine($"[VirtualDevice] Send failed: {result}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualDevice] Error sending MIDI: {ex.Message}");
        }
    }

    /// <summary>
    /// Convert UMP (Universal MIDI Packet) to MIDI 1.0 bytes.
    /// </summary>
    private static byte[]? UmpToMidi1(MidiMessageReceivedEventArgs args)
    {
        var word = args.PeekFirstWord();
        
        // UMP message type is in bits 28-31
        var messageType = (word >> 28) & 0x0F;
        
        // We only handle Type 2 = MIDI 1.0 Channel Voice Message
        if (messageType != 2)
        {
            // Could be Type 0 (Utility), Type 1 (System), Type 3 (Data), Type 4 (MIDI 2.0 CV)
            // For now, we only support MIDI 1.0 channel voice
            return null;
        }

        // Type 2 format: 0x2GCC_DDDD
        // G = Group (4 bits)
        // CC = Status + Channel (8 bits)
        // DDDD = Data bytes (16 bits: data1 << 8 | data2)
        
        var status = (byte)((word >> 16) & 0xFF);
        var data1 = (byte)((word >> 8) & 0x7F);
        var data2 = (byte)(word & 0x7F);

        // Determine message length based on status byte
        var statusHigh = status & 0xF0;
        
        return statusHigh switch
        {
            0xC0 => [status, data1],           // Program Change (2 bytes)
            0xD0 => [status, data1],           // Channel Pressure (2 bytes)
            0xF0 => HandleSystemMessage(status, data1, data2),
            _ => [status, data1, data2]        // All others are 3 bytes
        };
    }

    /// <summary>
    /// Handle system messages (status 0xF0-0xFF).
    /// </summary>
    private static byte[]? HandleSystemMessage(byte status, byte data1, byte data2)
    {
        return status switch
        {
            0xF1 => [status, data1],           // Time Code Quarter Frame
            0xF2 => [status, data1, data2],    // Song Position Pointer
            0xF3 => [status, data1],           // Song Select
            0xF6 => [status],                  // Tune Request
            0xF8 => [status],                  // Timing Clock
            0xFA => [status],                  // Start
            0xFB => [status],                  // Continue
            0xFC => [status],                  // Stop
            0xFE => [status],                  // Active Sensing
            0xFF => [status],                  // System Reset
            _ => null                          // SysEx and others not supported yet
        };
    }

    /// <summary>
    /// Convert MIDI 1.0 bytes to UMP (Universal MIDI Packet).
    /// </summary>
    private static uint? Midi1ToUmp(byte[] data)
    {
        if (data.Length < 1)
            return null;

        var status = data[0];
        
        // System messages need special handling
        if (status >= 0xF0)
        {
            return Midi1SystemToUmp(data);
        }

        // Channel voice messages
        var data1 = data.Length > 1 ? data[1] : (byte)0;
        var data2 = data.Length > 2 ? data[2] : (byte)0;

        // UMP Type 2 = MIDI 1.0 Channel Voice Message
        // Format: 0x2GCC_DDDD where G=group, CC=status, DDDD=data
        uint ump = 0x20000000;  // Type 2, Group 0
        ump |= (uint)status << 16;
        ump |= (uint)data1 << 8;
        ump |= data2;

        return ump;
    }

    /// <summary>
    /// Convert MIDI 1.0 system messages to UMP.
    /// </summary>
    private static uint? Midi1SystemToUmp(byte[] data)
    {
        var status = data[0];
        
        // UMP Type 1 = System Common and Real-Time
        // Format: 0x1G0S_DDDD
        uint ump = 0x10000000;  // Type 1, Group 0
        
        switch (status)
        {
            case 0xF1: // Time Code Quarter Frame
            case 0xF3: // Song Select
                if (data.Length < 2) return null;
                ump |= (uint)status << 16;
                ump |= (uint)data[1] << 8;
                break;
                
            case 0xF2: // Song Position Pointer
                if (data.Length < 3) return null;
                ump |= (uint)status << 16;
                ump |= (uint)data[1] << 8;
                ump |= data[2];
                break;
                
            case 0xF6: // Tune Request
            case 0xF8: // Timing Clock
            case 0xFA: // Start
            case 0xFB: // Continue
            case 0xFC: // Stop
            case 0xFE: // Active Sensing
            case 0xFF: // System Reset
                ump |= (uint)status << 16;
                break;
                
            default:
                // SysEx not supported in single-word UMP
                return null;
        }

        return ump;
    }

    /// <summary>
    /// Dispose resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        
        _disposed = true;

        Console.WriteLine("[VirtualDevice] Disposing...");

        // Complete the channel
        _incomingMessages.Writer.Complete();

        // Disconnect endpoint
        if (_connection != null && _session != null)
        {
            try
            {
                _session.DisconnectEndpointConnection(_connection.ConnectionId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VirtualDevice] Error disconnecting: {ex.Message}");
            }
        }

        // Dispose session
        try
        {
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualDevice] Error disposing session: {ex.Message}");
        }

        // Dispose initializer
        try
        {
            _initializer?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VirtualDevice] Error disposing initializer: {ex.Message}");
        }

        Console.WriteLine("[VirtualDevice] Disposed");
    }
}
