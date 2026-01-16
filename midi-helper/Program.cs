using System.IO.Pipes;

namespace OpenControl.MidiHelper;

/// <summary>
/// midi-helper: Windows MIDI Services Virtual Device for Open Control Bridge
/// 
/// This helper creates a virtual MIDI device using Windows MIDI Services
/// and communicates with the Rust bridge via Named Pipe.
/// 
/// Usage:
///   midi-helper.exe [--device-name "Name"] [--product-id "ID"] [--pipe-name "Name"]
/// 
/// Arguments:
///   --device-name  Display name for the virtual device (default: "Open Control")
///   --product-id   Unique product instance ID (default: "OPENCTRL_001")
///   --pipe-name    Named pipe name (default: "open-control-midi")
///   --standalone   Run without pipe server (for testing)
/// </summary>
class Program
{
    const string DefaultDeviceName = "Open Control";
    const string DefaultProductId = "OPENCTRL_001";
    const string DefaultPipeName = "open-control-midi";

    static async Task<int> Main(string[] args)
    {
        // Parse arguments
        var deviceName = GetArg(args, "--device-name") ?? DefaultDeviceName;
        var productId = GetArg(args, "--product-id") ?? DefaultProductId;
        var pipeName = GetArg(args, "--pipe-name") ?? DefaultPipeName;
        var standalone = args.Contains("--standalone");

        Console.WriteLine("===========================================");
        Console.WriteLine("  Open Control - MIDI Helper");
        Console.WriteLine("===========================================");
        Console.WriteLine($"  Device Name: {deviceName}");
        Console.WriteLine($"  Product ID:  {productId}");
        Console.WriteLine($"  Pipe Name:   {pipeName}");
        Console.WriteLine($"  Mode:        {(standalone ? "Standalone" : "Bridge")}");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        // Create and initialize virtual device
        using var device = new VirtualDevice();
        
        if (!device.Initialize(deviceName, productId))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[FATAL] Failed to initialize virtual device");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Make sure Windows MIDI Services is installed:");
            Console.Error.WriteLine("  winget install Microsoft.WindowsMIDIServices");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Or download from:");
            Console.Error.WriteLine("  https://github.com/microsoft/MIDI/releases");
            Console.Error.WriteLine();
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"[OK] Virtual device '{deviceName}' is now active!");
        Console.WriteLine();

        // Setup cancellation
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            Console.WriteLine();
            Console.WriteLine("[INFO] Shutdown requested (Ctrl+C)");
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (standalone)
            {
                // Standalone mode: just keep the device alive and log incoming messages
                await RunStandaloneMode(device, cts.Token);
            }
            else
            {
                // Bridge mode: run pipe server
                await RunPipeServer(device, pipeName, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Unexpected error: {ex.Message}");
            return 1;
        }

        Console.WriteLine("[INFO] Shutting down...");
        return 0;
    }

    /// <summary>
    /// Run in standalone mode (for testing without bridge).
    /// </summary>
    static async Task RunStandaloneMode(VirtualDevice device, CancellationToken ct)
    {
        Console.WriteLine("[INFO] Running in standalone mode");
        Console.WriteLine("[INFO] Press Ctrl+C to exit");
        Console.WriteLine("[INFO] Incoming MIDI messages will be logged below:");
        Console.WriteLine();

        // Just log incoming messages
        await foreach (var midiData in device.IncomingMessages.ReadAllAsync(ct))
        {
            var hex = BitConverter.ToString(midiData).Replace("-", " ");
            var desc = DescribeMidi(midiData);
            Console.WriteLine($"[MIDI IN] {hex} ({desc})");
        }
    }

    /// <summary>
    /// Run the Named Pipe server for bridge communication.
    /// </summary>
    static async Task RunPipeServer(VirtualDevice device, string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"[PIPE] Creating pipe: \\\\.\\pipe\\{pipeName}");
            
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,  // Max 1 connection at a time
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            Console.WriteLine("[PIPE] Waiting for bridge connection...");
            
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            
            Console.WriteLine("[PIPE] Bridge connected!");

            try
            {
                await HandleConnection(server, device, ct);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[PIPE] Connection lost: {ex.Message}");
            }
            catch (ProtocolException ex)
            {
                Console.WriteLine($"[PIPE] Protocol error: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Console.WriteLine("[PIPE] Bridge disconnected");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Handle a single bridge connection.
    /// </summary>
    static async Task HandleConnection(NamedPipeServerStream pipe, VirtualDevice device, CancellationToken ct)
    {
        // Send Ready message
        await Protocol.SendReadyAsync(pipe, DefaultDeviceName, ct);
        Console.WriteLine("[PIPE] Sent Ready message");

        // Create tasks for bidirectional communication
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        var readTask = ReadFromBridge(pipe, device, linkedCts.Token);
        var writeTask = WriteToBridge(pipe, device, linkedCts.Token);

        // Wait for either task to complete (or fail)
        var completedTask = await Task.WhenAny(readTask, writeTask);
        
        // Cancel the other task
        linkedCts.Cancel();

        // Propagate any exception
        await completedTask;
    }

    /// <summary>
    /// Read messages from bridge and send to virtual device.
    /// </summary>
    static async Task ReadFromBridge(Stream pipe, VirtualDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await Protocol.ReceiveMessageAsync(pipe, ct);

            switch (message.Type)
            {
                case MessageType.MidiData:
                    // Send MIDI to DAWs via virtual device
                    device.SendMidi(message.Data);
                    var hex = BitConverter.ToString(message.Data).Replace("-", " ");
                    Console.WriteLine($"[MIDI OUT] {hex}");
                    break;

                case MessageType.Ping:
                    // Respond with pong
                    await Protocol.SendPongAsync(pipe, ct);
                    break;

                case MessageType.Shutdown:
                    // Graceful shutdown requested
                    Console.WriteLine("[PIPE] Shutdown requested by bridge");
                    return;

                default:
                    Console.WriteLine($"[PIPE] Unknown message type: 0x{(byte)message.Type:X2}");
                    break;
            }
        }
    }

    /// <summary>
    /// Write incoming MIDI messages from DAWs to bridge.
    /// </summary>
    static async Task WriteToBridge(Stream pipe, VirtualDevice device, CancellationToken ct)
    {
        await foreach (var midiData in device.IncomingMessages.ReadAllAsync(ct))
        {
            await Protocol.SendMidiDataAsync(pipe, midiData, ct);
            var hex = BitConverter.ToString(midiData).Replace("-", " ");
            Console.WriteLine($"[MIDI IN] {hex}");
        }
    }

    /// <summary>
    /// Parse command line argument.
    /// </summary>
    static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Describe a MIDI message for logging.
    /// </summary>
    static string DescribeMidi(byte[] data)
    {
        if (data.Length == 0)
            return "Empty";

        var status = data[0];
        var channel = (status & 0x0F) + 1;
        var statusHigh = status & 0xF0;

        return statusHigh switch
        {
            0x80 => $"Note Off ch{channel} note={data.ElementAtOrDefault(1)} vel={data.ElementAtOrDefault(2)}",
            0x90 => data.ElementAtOrDefault(2) == 0 
                ? $"Note Off ch{channel} note={data.ElementAtOrDefault(1)}" 
                : $"Note On ch{channel} note={data.ElementAtOrDefault(1)} vel={data.ElementAtOrDefault(2)}",
            0xA0 => $"Poly Pressure ch{channel} note={data.ElementAtOrDefault(1)} pressure={data.ElementAtOrDefault(2)}",
            0xB0 => $"CC ch{channel} cc={data.ElementAtOrDefault(1)} val={data.ElementAtOrDefault(2)}",
            0xC0 => $"Program Change ch{channel} prog={data.ElementAtOrDefault(1)}",
            0xD0 => $"Channel Pressure ch{channel} pressure={data.ElementAtOrDefault(1)}",
            0xE0 => $"Pitch Bend ch{channel} val={data.ElementAtOrDefault(1) | (data.ElementAtOrDefault(2) << 7)}",
            0xF0 => status switch
            {
                0xF0 => "SysEx Start",
                0xF7 => "SysEx End",
                0xF8 => "Clock",
                0xFA => "Start",
                0xFB => "Continue",
                0xFC => "Stop",
                0xFE => "Active Sensing",
                0xFF => "System Reset",
                _ => $"System 0x{status:X2}"
            },
            _ => $"Unknown 0x{status:X2}"
        };
    }
}
