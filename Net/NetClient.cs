using System.Collections.Concurrent;
using System.Net.Sockets;

namespace AchtungDieKurve.Net;

/// <summary>
/// Connection to a <see cref="GameServer"/>. <see cref="Poll"/> (once per frame) applies the server's
/// lobby and snapshots to <see cref="Lobby"/> and the mirror <see cref="Match"/>, which the UI draws.
/// </summary>
public sealed class NetClient : IDisposable
{
    private const int ConnectTimeoutMs = 5000;

    private readonly ConcurrentQueue<Received> _inbox = new();
    private readonly Connection _connection;
    private readonly sbyte[] _steer;
    private long _nextPing;

    private NetClient(TcpClient tcp, int localPlayers, string address)
    {
        Address = address;
        LocalPlayers = localPlayers;
        _steer = new sbyte[localPlayers];
        _connection = new Connection(tcp, _inbox);
        _connection.Start();
    }

    public string Address { get; }
    public int LocalPlayers { get; }
    public int ClientId { get; private set; } = -1;
    public bool Welcomed => ClientId >= 0;
    public LobbyState Lobby { get; private set; } = new();
    /// <summary>Created on Welcome with the server's field size.</summary>
    public Match? Match { get; private set; }
    /// <summary>Set when the connection ended or was refused; the UI goes back to the menu.</summary>
    public string? Error { get; private set; }

    public bool IsHost => Lobby.Client(ClientId)?.IsHost == true;

    /// <summary>Connects and says hello; throws with a readable message on failure.</summary>
    public static async Task<NetClient> ConnectAsync(string address, int defaultPort, string name, int localPlayers, CancellationToken token)
    {
        if (!Protocol.TryParseAddress(address, defaultPort, out var host, out var port))
            throw new ArgumentException($"'{address}' is not a valid address (e.g. 192.168.1.20 or myserver:7777)");

        var tcp = new TcpClient();   // tries every address the name resolves to (IPv4 and IPv6)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ConnectTimeoutMs);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new IOException($"No answer from {host}:{port}");
        }
        catch (SocketException e)
        {
            tcp.Dispose();
            throw new IOException(e.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"No game running on {host}:{port}",
                SocketError.HostNotFound or SocketError.NoData => $"Unknown server '{host}'",
                _ => $"Cannot reach {host}:{port} ({e.Message})",
            });
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var client = new NetClient(tcp, localPlayers, $"{host}:{port}");
        client.Send(MsgType.Hello, w =>
        {
            w.Write(Protocol.Version);
            w.Write(name);
            w.Write((byte)localPlayers);
        });
        return client;
    }

    public void Dispose() => _connection.Close(null);

    public void Send(MsgType type, Action<BinaryWriter>? body = null) => _connection.Send(Protocol.Frame(type, body));

    /// <summary>Sends this PC's steering (only when it changed).</summary>
    public void SendSteer(ReadOnlySpan<float> steer)
    {
        var changed = false;
        for (var i = 0; i < _steer.Length; i++)
        {
            var v = (sbyte)(i < steer.Length ? Math.Sign(steer[i]) : 0);
            if (v == _steer[i]) continue;
            _steer[i] = v;
            changed = true;
        }
        if (changed) Send(MsgType.Steer, w => { w.Write((byte)_steer.Length); foreach (var v in _steer) w.Write(v); });
    }

    /// <summary>Applies everything received since the last frame.</summary>
    public void Poll()
    {
        while (Error is null && _inbox.TryDequeue(out var msg))
        {
            try
            {
                Apply(msg);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                Console.Error.WriteLine($"Invalid {msg.Type} message from the server: {e}");
                Error = "The server sent invalid data";
                _connection.Close(Error);
            }
        }

        if (Error is null && _connection.MillisSinceLastReceived > Protocol.TimeoutMs)
        {
            Error = "Connection lost (server not responding)";
            _connection.Close(Error);
        }

        var now = Environment.TickCount64;
        if (now < _nextPing) return;
        _nextPing = now + Protocol.PingIntervalMs;
        Send(MsgType.Ping);
    }

    private void Apply(Received msg)
    {
        using var r = msg.Reader();
        switch (msg.Type)
        {
            case MsgType.Closed:
                Error ??= _connection.CloseReason switch
                {
                    "disconnected" or "connection lost" => "Connection to the server lost",
                    { } reason => $"Disconnected: {reason}",
                    null => "Disconnected",
                };
                break;

            case MsgType.Reject:
                Error = Protocol.ReadName(r, 120);
                _connection.Close(Error);
                break;

            case MsgType.Welcome:
                ClientId = r.ReadInt32();
                int w = r.ReadInt16(), h = r.ReadInt16();
                if (w is < 200 or > 4000 || h is < 200 or > 4000) throw new InvalidDataException("bad field size");
                Match = new Match(w, h);
                break;

            case MsgType.Lobby:
                Lobby = LobbyState.Read(r);
                ApplySlots();
                break;

            case MsgType.Snapshot when Match is not null:
                Snapshot.Apply(r, Match);
                break;
        }
    }

    /// <summary>Copies who-plays-what into the mirror players (for names, labels and colours).</summary>
    private void ApplySlots()
    {
        if (Match is null) return;
        Match.AiLevel = Lobby.AiLevel;
        Match.PowerUpsEnabled = Lobby.PowerUps;
        foreach (var p in Match.Players)
        {
            var s = Lobby.Slots[p.Id];
            p.Kind = s.Kind;
            p.OwnerClient = s.Kind == PlayerKind.Human ? s.Owner : -1;
            p.KeySet = s.Kind == PlayerKind.Human ? s.KeySet : -1;
            p.HumanIndex = p.OwnerClient == ClientId ? p.KeySet : -1;   // local player k uses pad k
        }
    }
}
