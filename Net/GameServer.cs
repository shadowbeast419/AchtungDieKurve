using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AchtungDieKurve.Net;

/// <summary>
/// Authoritative game server: runs the lobby and the <see cref="Match"/> at 120 Hz on its own thread
/// and streams snapshots to every connected PC. Used headless (--server) and in-process ("Host game").
/// The first PC that joined is the host and the only one allowed to change settings.
/// </summary>
public sealed class GameServer : IDisposable
{
    private const int MaxConnections = 8;
    private const int SnapshotEveryTicks = 2;       // 60 snapshots per second
    private const float RoundOverSeconds = 4f;      // then the next round starts by itself
    private const int HelloTimeoutMs = 5000;

    private sealed class Client(Connection connection, int id)
    {
        public Connection Connection { get; } = connection;
        public int Id { get; } = id;
        public long ConnectedAt { get; } = Environment.TickCount64;
        public bool Welcomed;
        public string Name = "";
        public int LocalPlayers = 1;
        public readonly float[] Steer = new float[Settings.MaxLocalPlayers];
    }

    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<Received> _inbox = new();
    private readonly ConcurrentQueue<Connection> _accepted = new();
    private readonly List<Client> _clients = [];    // in join order: the first welcomed one is the host
    private readonly LobbyState _lobby = new();
    private readonly Match _match = new(Match.OnlineFieldWidth, Match.FieldHeight);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private int _nextId = 1;
    private float _roundOverTimer;
    private bool _lobbyDirty;
    private long _nextPing;

    public GameServer(int port, AiLevel aiLevel, bool powerUps)
    {
        Port = port;
        _lobby.AiLevel = aiLevel;
        _lobby.PowerUps = powerUps;
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        try { _listener.Server.DualMode = true; }   // accept IPv4 and IPv6
        catch (Exception) { _listener = new TcpListener(IPAddress.Any, port); }
    }

    public int Port { get; }

    /// <summary>Starts listening; throws <see cref="SocketException"/> if the port is in use.</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
        _thread = new Thread(Run) { IsBackground = true, Name = "GameServer" };
        _thread.Start();
        Log($"Listening on port {Port} ({string.Join(", ", LocalAddresses())})");
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _listener.Stop();
        _thread?.Join(1000);
    }

    /// <summary>LAN addresses other PCs can connect to.</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        string[] virtualPrefixes = ["docker", "veth", "br-", "virbr", "lo"];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => !virtualPrefixes.Any(n.Name.StartsWith))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static void Log(string message) => Console.WriteLine($"[server {DateTime.Now:HH:mm:ss}] {message}");

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _accepted.Enqueue(new Connection(tcp, _inbox));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) { Log($"Accept failed: {e.Message}"); }
        }
    }

    // ───────────────────────────── Main loop ─────────────────────────────

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        var ticks = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                ProcessNetwork();
                if (_lobbyDirty) BroadcastLobby();

                var now = clock.Elapsed.TotalSeconds;
                if (now - next > 0.25) next = now;   // stalled (e.g. suspended): don't try to catch up
                while (next <= now)
                {
                    next += Match.Dt;
                    if (!_lobby.InGame) continue;
                    UpdateMatch();
                    if (++ticks % SnapshotEveryTicks == 0) BroadcastSnapshot();
                }

                Housekeeping();
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            Log($"Server crashed: {e}");
        }

        var bye = Protocol.Frame(MsgType.Reject, w => w.Write("The host closed the game"));
        foreach (var c in _clients) c.Connection.SendAndClose(bye, "server stopped");
    }

    private void UpdateMatch()
    {
        var before = _match.Phase;
        _match.Advance(Match.Dt, SteerOf);

        if (_match.Phase != before)
        {
            if (_match.Phase == Phase.RoundOver)
            {
                _roundOverTimer = RoundOverSeconds;
                Log($"Round {_match.Round}: {_match.RoundMessage} ({Scores()})");
            }
            else if (_match.Phase == Phase.GameOver)
            {
                Log($"Game over: {_match.Winner?.Name} wins ({Scores()})");
            }
        }

        if (_match.Phase == Phase.RoundOver && (_roundOverTimer -= Match.Dt) <= 0) _match.StartRound();
    }

    private string Scores() => string.Join(", ", _match.Active.OrderByDescending(p => p.Score).Select(p => $"{p.Name} {p.Score}"));

    private float SteerOf(Player p)
    {
        var owner = _clients.Find(c => c.Id == p.OwnerClient);
        return owner is not null && p.KeySet >= 0 && p.KeySet < owner.Steer.Length ? owner.Steer[p.KeySet] : 0f;
    }

    private void Housekeeping()
    {
        var now = Environment.TickCount64;
        foreach (var c in _clients)
        {
            if (!c.Welcomed && now - c.ConnectedAt > HelloTimeoutMs) c.Connection.Close("no hello");
            else if (c.Connection.MillisSinceLastReceived > Protocol.TimeoutMs) c.Connection.Close("timed out");
        }

        if (now < _nextPing) return;
        _nextPing = now + Protocol.PingIntervalMs;
        Broadcast(Protocol.Frame(MsgType.Ping));
    }

    // ───────────────────────────── Messages ─────────────────────────────

    private void ProcessNetwork()
    {
        while (_accepted.TryDequeue(out var connection))
        {
            if (_clients.Count >= MaxConnections)
            {
                connection.SendAndClose(Protocol.Frame(MsgType.Reject, w => w.Write("The server is full")), "server full");
                connection.Start();
                continue;
            }
            _clients.Add(new Client(connection, _nextId++));
            connection.Start();
        }

        while (_inbox.TryDequeue(out var msg))
        {
            var client = _clients.Find(c => c.Connection == msg.From);
            if (client is null) continue;
            if (msg.Type == MsgType.Closed)
            {
                RemoveClient(client);
                continue;
            }

            try
            {
                Handle(client, msg);
            }
            catch (Exception e)
            {
                Log($"Dropping {client.Connection.Remote}: bad {msg.Type} message ({e.GetType().Name}: {e.Message})");
                client.Connection.Close("invalid data");
            }
        }
    }

    private void Handle(Client client, Received msg)
    {
        using var r = msg.Reader();
        if (!client.Welcomed)
        {
            if (msg.Type != MsgType.Hello) throw new InvalidDataException($"{msg.Type} before hello");
            HandleHello(client, r);
            return;
        }

        switch (msg.Type)
        {
            case MsgType.Steer:
                int n = r.ReadByte();
                for (var i = 0; i < Math.Min(n, client.Steer.Length); i++) client.Steer[i] = Math.Clamp((int)r.ReadSByte(), -1, 1);
                return;
            case MsgType.Ping:
                return;
        }

        if (client != Host) return;   // everything below is for the host only
        switch (msg.Type)
        {
            case MsgType.SetSlot when !_lobby.InGame:
                SetSlot(r.ReadByte(), new SlotAssignment(LobbyState.ReadEnum<PlayerKind>(r.ReadByte()), r.ReadInt32(), r.ReadSByte()));
                break;
            case MsgType.SetOptions when !_lobby.InGame:
                _lobby.AiLevel = LobbyState.ReadEnum<AiLevel>(r.ReadByte());
                _lobby.PowerUps = r.ReadBoolean();
                _lobbyDirty = true;
                break;
            case MsgType.Start when !_lobby.InGame && _lobby.ActiveSlots >= 2:
                StartGame();
                break;
            case MsgType.Pause when _lobby.InGame:
                if (_match.Phase == Phase.Paused) _match.Resume();
                else _match.Pause();
                break;
            case MsgType.Next when _lobby.InGame:
                if (_match.Phase == Phase.RoundOver) _match.StartRound();
                else if (_match.Phase == Phase.GameOver) _match.NewGame();
                break;
            case MsgType.ToLobby when _lobby.InGame:
                _lobby.InGame = false;
                _lobbyDirty = true;
                Log("Back to the lobby");
                break;
        }
    }

    private Client? Host => _clients.FirstOrDefault(c => c.Welcomed);

    private void HandleHello(Client client, BinaryReader r)
    {
        var version = r.ReadInt32();
        if (version != Protocol.Version)
        {
            client.Connection.SendAndClose(Protocol.Frame(MsgType.Reject,
                w => w.Write($"Version mismatch (server {Protocol.Version}, yours {version}) - update the game")), "version mismatch");
            return;
        }

        client.Name = Protocol.ReadName(r, 24) is { Length: > 0 } name ? name : $"PC {client.Id}";
        client.LocalPlayers = Math.Clamp((int)r.ReadByte(), 1, Settings.MaxLocalPlayers);
        client.Welcomed = true;
        client.Connection.Send(Protocol.Frame(MsgType.Welcome, w =>
        {
            w.Write(client.Id);
            w.Write((short)Match.OnlineFieldWidth);
            w.Write((short)Match.FieldHeight);
        }));

        // Give the newcomer's players the first free colours (spectate if the game is running or full).
        if (!_lobby.InGame)
        {
            for (var k = 0; k < client.LocalPlayers; k++)
            {
                var slot = Array.FindIndex(_lobby.Slots, s => s.Kind == PlayerKind.Off);
                if (slot < 0) slot = Array.FindIndex(_lobby.Slots, s => s.Kind == PlayerKind.Ai);
                if (slot < 0) break;
                _lobby.Slots[slot] = new SlotAssignment(PlayerKind.Human, client.Id, k);
            }
        }

        _lobbyDirty = true;
        Log($"{client.Name} joined from {client.Connection.Remote} with {client.LocalPlayers} player(s)" +
            (client == Host ? " - host" : ""));
    }

    private void SetSlot(int slot, SlotAssignment value)
    {
        if (slot is < 0 or >= Settings.SlotCount) throw new InvalidDataException("bad slot");
        if (value.Kind == PlayerKind.Human)
        {
            var owner = _clients.Find(c => c.Welcomed && c.Id == value.Owner);
            if (owner is null || value.KeySet < 0 || value.KeySet >= owner.LocalPlayers) return;
            // A person can only drive one colour: swap with the slot that had them.
            var other = Array.IndexOf(_lobby.Slots, value);
            if (other >= 0) _lobby.Slots[other] = _lobby.Slots[slot];
        }
        else
        {
            value = value.Kind == PlayerKind.Ai ? SlotAssignment.Ai : SlotAssignment.Off;
        }
        _lobby.Slots[slot] = value;
        _lobbyDirty = true;
    }

    private void StartGame()
    {
        _match.Configure(_lobby.Slots.Select(s => s.Kind).ToArray(), _lobby.AiLevel, _lobby.PowerUps);
        foreach (var p in _match.Players)
        {
            var s = _lobby.Slots[p.Id];
            p.OwnerClient = s.Kind == PlayerKind.Human ? s.Owner : -1;
            p.KeySet = s.Kind == PlayerKind.Human ? s.KeySet : -1;
        }
        _match.Events.Clear();
        _match.NewGame();
        _lobby.InGame = true;
        _lobbyDirty = true;
        Log($"Game started: {string.Join(", ", _match.Active.Select(p => $"{p.Name} ({Describe(_lobby.Slots[p.Id])})"))}");
    }

    private string Describe(SlotAssignment s) =>
        s.Kind == PlayerKind.Ai ? $"AI {_lobby.AiLevel}" : $"{_clients.Find(c => c.Id == s.Owner)?.Name} #{s.KeySet + 1}";

    private void RemoveClient(Client client)
    {
        _clients.Remove(client);
        if (!client.Welcomed) return;
        Log($"{client.Name} left ({client.Connection.CloseReason ?? "quit"})");

        for (var i = 0; i < Settings.SlotCount; i++)
        {
            if (_lobby.Slots[i].Kind != PlayerKind.Human || _lobby.Slots[i].Owner != client.Id) continue;
            // Mid-game the computer takes over, so the others can finish; in the lobby the colour is freed.
            _lobby.Slots[i] = _lobby.InGame ? SlotAssignment.Ai : SlotAssignment.Off;
            if (_lobby.InGame) _match.MakeAi(_match.Players[i]);
        }

        if (_lobby.InGame && !_clients.Any(c => c.Welcomed))
        {
            _lobby.InGame = false;
            Log("Everyone left - back to the lobby");
        }
        _lobbyDirty = true;
    }

    // ───────────────────────────── Sending ─────────────────────────────

    private void Broadcast(byte[] frame)
    {
        foreach (var c in _clients)
            if (c.Welcomed) c.Connection.Send(frame);
    }

    private void BroadcastLobby()
    {
        _lobbyDirty = false;
        var host = Host;
        _lobby.Clients = _clients.Where(c => c.Welcomed)
            .Select(c => new LobbyClient(c.Id, c.Name, c.LocalPlayers, c == host)).ToList();
        Broadcast(Protocol.Frame(MsgType.Lobby, _lobby.Write));
    }

    private void BroadcastSnapshot()
    {
        Broadcast(Protocol.Frame(MsgType.Snapshot, w => Snapshot.Write(w, _match, _match.Events)));
        _match.Events.Clear();
    }
}
