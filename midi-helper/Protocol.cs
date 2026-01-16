using System.Text;

namespace OpenControl.MidiHelper;

/// <summary>
/// Message types for Named Pipe communication between Bridge and Helper.
/// </summary>
public enum MessageType : byte
{
    /// <summary>MIDI data (Bridge -> Helper or Helper -> Bridge)</summary>
    MidiData = 0x01,
    
    /// <summary>Keepalive ping (Bridge -> Helper)</summary>
    Ping = 0x02,
    
    /// <summary>Keepalive pong response (Helper -> Bridge)</summary>
    Pong = 0x02,
    
    /// <summary>Device ready notification (Helper -> Bridge)</summary>
    Ready = 0x10,
    
    /// <summary>Error message (Helper -> Bridge)</summary>
    Error = 0xFE,
    
    /// <summary>MIDI service not available (Helper -> Bridge)</summary>
    ServiceUnavailable = 0xFD,
    
    /// <summary>Shutdown request (Bridge -> Helper)</summary>
    Shutdown = 0xFF
}

/// <summary>
/// Parsed message from the pipe.
/// </summary>
public readonly record struct Message(MessageType Type, byte[] Data);

/// <summary>
/// Protocol implementation for length-prefixed binary messages over Named Pipe.
/// 
/// Wire format:
/// [Length: 4 bytes, little-endian] [Payload: N bytes]
/// 
/// Payload format:
/// [MessageType: 1 byte] [Data: N-1 bytes]
/// </summary>
public static class Protocol
{
    private const int MaxMessageSize = 1024 * 1024; // 1MB max

    /// <summary>
    /// Send a "Ready" message with the device name.
    /// </summary>
    public static async Task SendReadyAsync(Stream stream, string deviceName, CancellationToken ct = default)
    {
        var nameBytes = Encoding.UTF8.GetBytes(deviceName);
        var payload = new byte[1 + nameBytes.Length];
        payload[0] = (byte)MessageType.Ready;
        nameBytes.CopyTo(payload, 1);
        await SendRawAsync(stream, payload, ct);
    }

    /// <summary>
    /// Send a "Pong" response to a ping.
    /// </summary>
    public static async Task SendPongAsync(Stream stream, CancellationToken ct = default)
    {
        await SendRawAsync(stream, [(byte)MessageType.Pong], ct);
    }

    /// <summary>
    /// Send MIDI data to the bridge.
    /// </summary>
    public static async Task SendMidiDataAsync(Stream stream, byte[] midiData, CancellationToken ct = default)
    {
        var payload = new byte[1 + midiData.Length];
        payload[0] = (byte)MessageType.MidiData;
        midiData.CopyTo(payload, 1);
        await SendRawAsync(stream, payload, ct);
    }

    /// <summary>
    /// Send an error message.
    /// </summary>
    public static async Task SendErrorAsync(Stream stream, string message, CancellationToken ct = default)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + msgBytes.Length];
        payload[0] = (byte)MessageType.Error;
        msgBytes.CopyTo(payload, 1);
        await SendRawAsync(stream, payload, ct);
    }

    /// <summary>
    /// Send a "Service Unavailable" message.
    /// </summary>
    public static async Task SendServiceUnavailableAsync(Stream stream, CancellationToken ct = default)
    {
        await SendRawAsync(stream, [(byte)MessageType.ServiceUnavailable], ct);
    }

    /// <summary>
    /// Receive and parse a message from the pipe.
    /// </summary>
    public static async Task<Message> ReceiveMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var payload = await ReceiveRawAsync(stream, ct);
        
        if (payload.Length == 0)
            throw new ProtocolException("Empty message received");

        var type = (MessageType)payload[0];
        var data = payload.Length > 1 ? payload[1..] : [];
        
        return new Message(type, data);
    }

    /// <summary>
    /// Send raw bytes with length prefix.
    /// </summary>
    private static async Task SendRawAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var lenBytes = BitConverter.GetBytes((uint)payload.Length);
        
        // Ensure little-endian
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(lenBytes);
        
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Receive raw bytes with length prefix.
    /// </summary>
    private static async Task<byte[]> ReceiveRawAsync(Stream stream, CancellationToken ct)
    {
        // Read length (4 bytes)
        var lenBytes = new byte[4];
        await ReadExactlyAsync(stream, lenBytes, ct);
        
        // Ensure little-endian
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(lenBytes);
        
        var len = BitConverter.ToUInt32(lenBytes);
        
        // Sanity check
        if (len > MaxMessageSize)
            throw new ProtocolException($"Message too large: {len} bytes (max: {MaxMessageSize})");

        // Read payload
        var payload = new byte[len];
        await ReadExactlyAsync(stream, payload, ct);
        
        return payload;
    }

    /// <summary>
    /// Read exactly the specified number of bytes.
    /// </summary>
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            
            if (read == 0)
                throw new EndOfStreamException("Pipe closed unexpectedly");
            
            offset += read;
        }
    }
}

/// <summary>
/// Exception thrown when protocol communication fails.
/// </summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
