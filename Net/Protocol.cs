using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace AchtungDieKurve.Net;

public enum MsgType : byte
{
    /// <summary>Not sent: queued locally when a connection ends.</summary>
    Closed = 0,

    // Client → server
    Hello = 1,      // int version, string name, byte localPlayers
    Steer,          // byte n, sbyte[n] (-1/0/1 per local player)
    SetSlot,        // byte slot, byte kind, int owner, byte keySet   (host only)
    SetOptions,     // byte aiLevel, bool powerUps                     (host only)
    Start,          // host only
    Pause,          // host only: toggles pause
    Next,           // host only: next round / new game
    ToLobby,        // host only
    Ping,

    // Server → client
    Welcome = 32,   // int clientId, short fieldWidth, short fieldHeight
    Reject,         // string reason (connection is closed afterwards)
    Lobby,          // LobbyState
    Snapshot,       // see GameServer.WriteSnapshot
}

/// <summary>
/// Wire format: [int32 length][byte type][payload], little-endian, over TCP.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MaxMessage = 64 * 1024;
    public const int TimeoutMs = 10_000;
    public const int PingIntervalMs = 2_000;

    public static byte[] Frame(MsgType type, Action<BinaryWriter>? body = null)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0);   // length, patched below
            w.Write((byte)type);
            body?.Invoke(w);
        }
        var frame = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 4);
        return frame;
    }

    /// <summary>Splits "host", "host:port", "[ipv6]:port" or "tcp://host:port/" into host and port.</summary>
    public static bool TryParseAddress(string text, int defaultPort, out string host, out int port)
    {
        host = text.Trim();
        port = defaultPort;
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];

        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0) return false;
            var rest = host[(end + 1)..];
            host = host[1..end];
            if (rest.StartsWith(':') && !int.TryParse(rest[1..], out port)) return false;
        }
        else if (host.Count(c => c == ':') == 1)
        {
            var colon = host.IndexOf(':');
            if (!int.TryParse(host[(colon + 1)..], out port)) return false;
            host = host[..colon];
        }
        return host.Length > 0 && port is > 0 and < 65536;
    }

    public static string ReadName(BinaryReader r, int maxLength)
    {
        var s = new string(r.ReadString().Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > maxLength ? s[..maxLength] : s;
    }
}

public readonly record struct Received(Connection From, MsgType Type, byte[] Data)
{
    /// <summary>Reader over the payload (after the type byte).</summary>
    public BinaryReader Reader() => new(new MemoryStream(Data, 1, Data.Length - 1, writable: false));
}

/// <summary>
/// One TCP connection: a read loop that queues incoming messages into a shared inbox and a write
/// loop fed by a channel, so the game loop never blocks on the network.
/// </summary>
public sealed class Connection
{
    private const int MaxQueuedFrames = 1000;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ConcurrentQueue<Received> _inbox;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;
    private int _queued;
    private long _lastReceived = Environment.TickCount64;

    public Connection(TcpClient tcp, ConcurrentQueue<Received> inbox)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _inbox = inbox;
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    /// <summary>Starts reading and writing; call once the owner knows this connection.</summary>
    public void Start()
    {
        _ = ReadLoop();
        _ = WriteLoop();
    }

    public string Remote { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public string? CloseReason { get; private set; }
    public long MillisSinceLastReceived => Environment.TickCount64 - Interlocked.Read(ref _lastReceived);

    public void Send(byte[] frame)
    {
        if (IsClosed) return;
        if (Interlocked.Increment(ref _queued) > MaxQueuedFrames) { Close("connection too slow"); return; }
        _out.Writer.TryWrite(frame);
    }

    /// <summary>Sends a last message, then closes.</summary>
    public void SendAndClose(byte[] frame, string reason)
    {
        CloseReason ??= reason;
        Interlocked.Increment(ref _queued);
        _out.Writer.TryWrite(frame);
        _out.Writer.TryComplete();
    }

    public void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CloseReason ??= reason;
        _out.Writer.TryComplete();
        _cts.Cancel();
        _tcp.Dispose();
        _inbox.Enqueue(new Received(this, MsgType.Closed, [0]));
    }

    private async Task ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 1 or > Protocol.MaxMessage) { Close("invalid data"); return; }
                var data = new byte[length];
                await _stream.ReadExactlyAsync(data, _cts.Token);
                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                _inbox.Enqueue(new Received(this, (MsgType)data[0], data));
            }
        }
        catch (EndOfStreamException) { Close("disconnected"); }
        catch (OperationCanceledException) { Close(null); }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { Close("connection lost"); }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(_cts.Token))
            {
                Interlocked.Decrement(ref _queued);
                await _stream.WriteAsync(frame, _cts.Token);
            }
            await _stream.FlushAsync(_cts.Token);
            Close(null);   // the channel was completed (SendAndClose)
        }
        catch (OperationCanceledException) { Close(null); }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { Close("connection lost"); }
    }
}
