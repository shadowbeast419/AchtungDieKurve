using System.Net.Sockets;
using System.Numerics;
using AchtungDieKurve.Net;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace AchtungDieKurve;

/// <summary>How the game was asked to start from the command line.</summary>
public sealed record NetStart(bool Host, string? Address);

/// <summary>
/// Window, menus, input and drawing. The rules live in <see cref="Match"/>: locally the game runs its
/// own match; online it draws the mirror match of a <see cref="NetClient"/>.
/// Everything is laid out in logical units: the height is always 800, the width grows with the
/// window's aspect ratio (min. 1280), so 16:9 screens like QHD get a wider field instead of bars.
/// The logical view is scaled to the window; trails are rendered at native resolution.
/// </summary>
public sealed class Game : IDisposable
{
    public const int BaseWidth = 1280;
    public const int BaseHeight = 800;

    private const int FieldX = 10;
    private const int FieldY = 10;
    private const int FieldH = Match.FieldHeight;
    private const int PanelWidth = 310;
    private const int TickRate = Match.TickRate;
    private const float Radius = Match.Radius;
    private const int PopupTicks = TickRate * 3 / 2;

    private static readonly Color Background = Color.Black;
    private static readonly Color Border = new(230, 210, 40, 255);
    private static readonly Color HeadColor = new(255, 235, 60, 255);
    private static readonly Color Selection = new(60, 60, 30, 255);
    private static readonly Color ErrorColor = new(255, 110, 110, 255);

    private enum Screen { MainMenu, LocalSetup, Join, Connecting, Lobby, InGame }

    private enum MainRow { Local, Host, Join, LocalPlayers, Resolution, Quit }
    private enum SetupRow { Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, AiLevel, PowerUps, Resolution, Start }
    private enum JoinRow { Address, Connect }
    private enum LobbyRow { Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, AiLevel, PowerUps, Start }

    private sealed record Popup(PowerUpType Type, Vector2 Position, int StartTick);

    private sealed record MenuItem(string Label, string Value, Color LabelColor, Color ValueColor, bool Button = false);

    private readonly Settings _settings;
    private readonly bool _demo;
    private readonly int _demoRounds;
    private readonly float _timeScale;
    private readonly bool _screenshots;
    private readonly Match _local = new(1, 1);
    private readonly List<(Vector2 A, Vector2 B, Color Color)> _pendingSegments = [];
    private readonly List<Popup> _popups = [];
    private readonly List<float> _roundDurations = [];

    // Network
    private NetClient? _net;
    private GameServer? _server;
    private Task<NetClient>? _connecting;
    private CancellationTokenSource? _connectCancel;
    private string _connectTarget = "";
    private string[] _hostAddresses = [];
    private string _address = "";
    private bool _leavePrompt;

    // Layout, (re)built by EnsureLayout().
    private int _logicalW;
    private int _fieldW;
    private int _panelX;
    private RenderTexture2D _trails;
    private float _trailScale;
    private bool _hasTrails;

    private Screen _screen = Screen.MainMenu;
    private int _menuIndex;
    private float _autoTimer;
    private bool _screenshotPending;
    private string _message = "";
    private float _messageTimer;

    public bool QuitRequested { get; private set; }

    /// <summary>The match on screen: the server's mirror when online, else the local one.</summary>
    private Match M => _net?.Match ?? _local;

    private bool Online => _net is not null;

    /// <param name="demoLevel">If set, all six slots are AI of this level and the game runs by itself.</param>
    /// <param name="demoRounds">Demo only: quit after this many rounds and print statistics (0 = endless).</param>
    /// <param name="timeScale">Simulation speed multiplier (testing).</param>
    /// <param name="screenshots">Save a screenshot after every round (default: on, except in demo mode).</param>
    /// <param name="netStart">Host or join a network game right away.</param>
    public Game(Settings settings, AiLevel? demoLevel = null, int demoRounds = 0, float timeScale = 1f,
        bool? screenshots = null, NetStart? netStart = null)
    {
        _settings = settings;
        _timeScale = timeScale;
        _screenshots = screenshots ?? demoLevel is null;
        if (demoLevel is { } level)
        {
            _demo = true;
            _demoRounds = demoRounds;
            _settings = new Settings { Slots = Enumerable.Repeat(PlayerKind.Ai, Settings.SlotCount).ToArray(), AiLevel = level };
            _screen = Screen.LocalSetup;
        }
        ApplySettings();
        EnsureLayout();

        if (netStart is { Host: true }) HostGame();
        else if (netStart?.Address is { } address) Connect(address);
    }

    public void Dispose()
    {
        LeaveNetwork(null);
        if (_hasTrails) UnloadRenderTexture(_trails);
    }

    private void ApplySettings()
    {
        _local.Configure(_settings.Slots, _settings.AiLevel, _settings.PowerUps);
        foreach (var p in _local.Players) p.KeySet = p.Kind == PlayerKind.Human ? p.Id : -1;
    }

    private void SetScreen(Screen screen, int menuIndex = 0)
    {
        _screen = screen;
        _menuIndex = menuIndex;
        _leavePrompt = false;
        Input.FlushTyped();
    }

    private void ShowMessage(string message, float seconds = 4)
    {
        _message = message;
        _messageTimer = seconds;
    }

    // ───────────────────────────── Layout / scaling ─────────────────────────────

    private static int DesiredLogicalWidth() =>
        Math.Max(BaseWidth, (int)(BaseHeight * GetRenderWidth() / (float)Math.Max(1, GetRenderHeight())));

    private float ViewScale => MathF.Min(GetRenderWidth() / (float)_logicalW, GetRenderHeight() / (float)BaseHeight);

    /// <summary>
    /// Rebuilds field size and trail texture if the window changed. Online the field size is the
    /// server's (other aspect ratios get bars). Only called between rounds, so a running round is never
    /// disturbed (it is just rescaled).
    /// </summary>
    private void EnsureLayout()
    {
        var logicalW = _net?.Match is { } online ? online.FieldWidth + FieldX + PanelWidth : DesiredLogicalWidth();
        if (logicalW != _logicalW)
        {
            _logicalW = logicalW;
            _fieldW = _logicalW - FieldX - PanelWidth;
            _panelX = FieldX + _fieldW + 20;
            if (!Online) _local.Resize(_fieldW, FieldH);
            _trailScale = 0; // force texture rebuild
        }

        var scale = ViewScale;
        if (MathF.Abs(scale - _trailScale) > 0.01f)
        {
            if (_hasTrails) UnloadRenderTexture(_trails);
            _trailScale = scale;
            _trails = LoadRenderTexture((int)MathF.Ceiling(_fieldW * scale), (int)MathF.Ceiling(FieldH * scale));
            SetTextureFilter(_trails.Texture, TextureFilter.Bilinear);
            _hasTrails = true;
            ClearTrails();
        }
    }

    // ───────────────────────────── Update ─────────────────────────────

    public void Update(float frameTime)
    {
        Input.UpdatePads();
        frameTime *= _timeScale;
        _messageTimer -= frameTime;

        if (_demo && IsKeyPressed(KeyboardKey.Escape)) { QuitRequested = true; return; }
        if (Input.FullscreenPressed()) Display.ToggleFullscreen();

        if (_net is { } net)
        {
            net.Poll();
            if (net.Error is { } error) LeaveNetwork(error);
        }

        switch (_screen)
        {
            case Screen.MainMenu: EnsureLayout(); UpdateMainMenu(); break;
            case Screen.LocalSetup: EnsureLayout(); UpdateLocalSetup(); break;
            case Screen.Join: EnsureLayout(); UpdateJoin(); break;
            case Screen.Connecting: EnsureLayout(); UpdateConnecting(); break;
            case Screen.Lobby: EnsureLayout(); UpdateLobby(); break;
            case Screen.InGame when Online: UpdateOnlineGame(); break;
            case Screen.InGame: UpdateLocalGame(frameTime); break;
        }

        DrainEvents();
    }

    private void UpdateMainMenu()
    {
        var count = Enum.GetValues<MainRow>().Length;
        _menuIndex = (_menuIndex + Input.MenuDy + count) % count;
        var row = (MainRow)_menuIndex;
        if (Input.MenuDx != 0) ChangeMainValue(row, Input.MenuDx);

        if (Input.ConfirmPressed())
        {
            switch (row)
            {
                case MainRow.Local: SetScreen(Screen.LocalSetup, (int)SetupRow.Start); break;
                case MainRow.Host: HostGame(); break;
                case MainRow.Join:
                    _address = _settings.LastServer;
                    SetScreen(Screen.Join);
                    break;
                case MainRow.Quit: QuitRequested = true; break;
                default: ChangeMainValue(row, 1); break;
            }
        }
        // Only the keyboard quits, so a stray B press on a pad can't close the game.
        else if (IsKeyPressed(KeyboardKey.Escape)) QuitRequested = true;
    }

    private void ChangeMainValue(MainRow row, int delta)
    {
        switch (row)
        {
            case MainRow.LocalPlayers:
                _settings.LocalPlayers = (_settings.LocalPlayers - 1 + delta + Settings.MaxLocalPlayers) % Settings.MaxLocalPlayers + 1;
                _settings.Save();
                break;
            case MainRow.Resolution:
                Display.CycleResolution();
                break;
        }
    }

    private void UpdateLocalSetup()
    {
        if (_demo) { NewLocalGame(); return; }

        var count = Enum.GetValues<SetupRow>().Length;
        _menuIndex = (_menuIndex + Input.MenuDy + count) % count;
        if (Input.MenuDx != 0) ChangeSetupValue((SetupRow)_menuIndex, Input.MenuDx);

        if (Input.ConfirmPressed())
        {
            if (_local.Players.Count(p => p.Active) < 2)
            {
                ShowMessage("At least 2 players needed - set someone to Human or AI", 3);
            }
            else
            {
                _settings.Save();
                NewLocalGame();
            }
        }
        else if (Input.BackPressed()) SetScreen(Screen.MainMenu);
    }

    private void ChangeSetupValue(SetupRow row, int delta)
    {
        switch (row)
        {
            case <= SetupRow.Slot5:
            {
                var slot = (int)row;
                PlayerKind[] options = slot < Settings.HumanSlots
                    ? [PlayerKind.Human, PlayerKind.Ai, PlayerKind.Off]
                    : [PlayerKind.Ai, PlayerKind.Off];
                var idx = Math.Max(0, Array.IndexOf(options, _settings.Slots[slot]));
                _settings.Slots[slot] = options[(idx + delta + options.Length) % options.Length];
                break;
            }
            case SetupRow.AiLevel:
                _settings.AiLevel = (AiLevel)(((int)_settings.AiLevel + delta + 3) % 3);
                break;
            case SetupRow.PowerUps:
                _settings.PowerUps = !_settings.PowerUps;
                break;
            case SetupRow.Resolution:
                Display.CycleResolution();
                break;
        }
        ApplySettings();
    }

    // ───────────────────────────── Local game ─────────────────────────────

    private void NewLocalGame()
    {
        ApplySettings();
        EnsureLayout();
        _local.NewGame();
        _popups.Clear();
        SetScreen(Screen.InGame);
    }

    private void StartLocalRound()
    {
        EnsureLayout();
        _local.StartRound();
    }

    private void UpdateLocalGame(float frameTime)
    {
        switch (_local.Phase)
        {
            case Phase.Prepare when !_demo && Input.PausePressed():
                SetScreen(Screen.LocalSetup, (int)SetupRow.Start);
                return;
            case Phase.Playing when !_demo && Input.PausePressed():
                _local.Pause();
                return;
            case Phase.RoundOver when _demo ? (_autoTimer -= frameTime) <= 0 : Input.ConfirmPressed():
                StartLocalRound();
                return;
            case Phase.Paused when Input.ConfirmPressed():
                _local.Resume();
                return;
            case Phase.GameOver when _demo ? (_autoTimer -= frameTime) <= 0 : Input.ConfirmPressed():
                NewLocalGame();
                return;
            case Phase.RoundOver or Phase.Paused or Phase.GameOver when !_demo && Input.BackPressed():
                SetScreen(Screen.LocalSetup, (int)SetupRow.Start);
                return;
        }

        _local.Advance(frameTime, p => Input.Steering(p.KeySet, p.HumanIndex), 0.25f * _timeScale);
    }

    private void OnRoundEnd()
    {
        if (_screenshots) _screenshotPending = true;
        if (!_demo || QuitRequested) return;

        _autoTimer = M.Phase == Phase.GameOver ? 3 : 2;
        var seconds = M.Tick / (float)TickRate;
        _roundDurations.Add(seconds);
        Console.WriteLine($"Round {M.Round}: {seconds,5:0.0}s  final speed x{M.RoundSpeed:0.00}  " +
                          $"power-ups {M.RoundPickups}  winner {M.Active.FirstOrDefault(p => p.Alive)?.Name ?? "-"}");
        if (M.Winner is { } winner)
            Console.WriteLine($"=== {winner.Name} wins the game: " +
                              string.Join(", ", M.Active.OrderByDescending(p => p.Score).Select(p => $"{p.Name} {p.Score}")));

        if (_demoRounds > 0 && _roundDurations.Count >= _demoRounds)
        {
            Console.WriteLine($"Average round length ({_settings.AiLevel}, {_roundDurations.Count} rounds): " +
                              $"{_roundDurations.Average():0.0}s");
            QuitRequested = true;
        }
    }

    // ───────────────────────────── Network ─────────────────────────────

    private void HostGame()
    {
        try
        {
            _server = new GameServer(_settings.Port, _settings.AiLevel, _settings.PowerUps);
            _server.Start();
        }
        catch (SocketException e)
        {
            _server?.Dispose();
            _server = null;
            ShowMessage(e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {_settings.Port} is already in use - is another game server running?"
                : $"Cannot start the server: {e.Message}");
            return;
        }
        _hostAddresses = GameServer.LocalAddresses().Select(a => $"{a}:{_settings.Port}").ToArray();
        Connect($"127.0.0.1:{_settings.Port}");
    }

    private void Connect(string address)
    {
        if (_server is null)
        {
            _settings.LastServer = address;
            _settings.Save();
        }
        _connectTarget = address;
        _connectCancel = new CancellationTokenSource();
        _connecting = NetClient.ConnectAsync(address, _settings.Port, _settings.PlayerName, _settings.LocalPlayers, _connectCancel.Token);
        SetScreen(Screen.Connecting);
    }

    /// <summary>Disconnects (and stops our own server); back to the main menu.</summary>
    private void LeaveNetwork(string? message)
    {
        var wasOnline = Online || _connecting is not null;
        _connectCancel?.Cancel();
        _connecting = null;
        _net?.Dispose();
        _net = null;
        _server?.Dispose();
        _server = null;
        _popups.Clear();
        if (!wasOnline) return;

        SetScreen(Screen.MainMenu, (int)MainRow.Join);
        if (message is null) return;
        Console.WriteLine($"Left the network game: {message}");
        ShowMessage(message, 6);
    }

    private void UpdateJoin()
    {
        var count = Enum.GetValues<JoinRow>().Length;
        _menuIndex = (_menuIndex + Input.MenuDy + count) % count;
        if ((JoinRow)_menuIndex == JoinRow.Address)
            _address = Input.TextInput(_address, 80, c => char.IsLetterOrDigit(c) || ".:-_/[]".Contains(c));
        else
            Input.FlushTyped();

        if (Input.ConfirmPressed())
        {
            if (string.IsNullOrWhiteSpace(_address)) ShowMessage("Type the address of the PC running the game", 3);
            else Connect(_address.Trim());
        }
        else if (Input.BackPressed()) SetScreen(Screen.MainMenu, (int)MainRow.Join);
    }

    private void UpdateConnecting()
    {
        if (Input.BackPressed())
        {
            LeaveNetwork(null);
            return;
        }
        if (_connecting is not { IsCompleted: true } task) return;
        _connecting = null;

        if (task.IsCompletedSuccessfully)
        {
            _net = task.Result;
            SetScreen(Screen.Lobby, (int)LobbyRow.Start);
            return;
        }

        var error = task.Exception?.InnerException?.Message ?? "Connection failed";
        var hosting = _server is not null;
        LeaveNetwork(null);
        if (!hosting) SetScreen(Screen.Join, (int)JoinRow.Connect);
        ShowMessage(error, 6);
    }

    private void UpdateLobby()
    {
        if (_net is not { Welcomed: true } net) return;
        if (net.Lobby.InGame)
        {
            SetScreen(Screen.InGame);
            return;
        }
        if (Input.BackPressed())
        {
            LeaveNetwork(null);
            return;
        }
        if (!net.IsHost) return;

        var count = Enum.GetValues<LobbyRow>().Length;
        _menuIndex = (_menuIndex + Input.MenuDy + count) % count;
        var row = (LobbyRow)_menuIndex;
        if (Input.MenuDx != 0) ChangeLobbyValue(net, row, Input.MenuDx);

        if (Input.ConfirmPressed())
        {
            if (net.Lobby.ActiveSlots < 2) ShowMessage("At least 2 players needed - give a colour to a PC or the AI", 3);
            else net.Send(MsgType.Start);
        }
    }

    private void ChangeLobbyValue(NetClient net, LobbyRow row, int delta)
    {
        var lobby = net.Lobby;
        switch (row)
        {
            case <= LobbyRow.Slot5:
            {
                // Off → AI → every player of every connected PC.
                var options = new List<SlotAssignment> { SlotAssignment.Off, SlotAssignment.Ai };
                foreach (var c in lobby.Clients)
                    for (var k = 0; k < c.LocalPlayers; k++)
                        options.Add(new SlotAssignment(PlayerKind.Human, c.Id, k));
                var slot = (int)row;
                var idx = Math.Max(0, options.IndexOf(lobby.Slots[slot]));
                var value = options[(idx + delta + options.Count) % options.Count];
                net.Send(MsgType.SetSlot, w =>
                {
                    w.Write((byte)slot);
                    w.Write((byte)value.Kind);
                    w.Write(value.Owner);
                    w.Write((sbyte)value.KeySet);
                });
                break;
            }
            case LobbyRow.AiLevel:
                SendOptions(net, (AiLevel)(((int)lobby.AiLevel + delta + 3) % 3), lobby.PowerUps);
                break;
            case LobbyRow.PowerUps:
                SendOptions(net, lobby.AiLevel, !lobby.PowerUps);
                break;
        }
    }

    private void SendOptions(NetClient net, AiLevel aiLevel, bool powerUps)
    {
        net.Send(MsgType.SetOptions, w =>
        {
            w.Write((byte)aiLevel);
            w.Write(powerUps);
        });
        // The host's choices are the defaults the next time they host.
        _settings.AiLevel = aiLevel;
        _settings.PowerUps = powerUps;
        _settings.Save();
    }

    private void UpdateOnlineGame()
    {
        var net = _net!;
        Span<float> steer = stackalloc float[net.LocalPlayers];
        for (var k = 0; k < steer.Length; k++) steer[k] = Input.Steering(k, k);
        net.SendSteer(steer);

        if (!net.Lobby.InGame)
        {
            SetScreen(Screen.Lobby, (int)LobbyRow.Start);
            return;
        }

        var phase = M.Phase;
        if (net.IsHost)
        {
            if (phase == Phase.Playing && Input.PausePressed()) net.Send(MsgType.Pause);
            else if (phase == Phase.Paused && Input.ConfirmPressed()) net.Send(MsgType.Pause);
            else if (phase is Phase.RoundOver or Phase.GameOver && Input.ConfirmPressed()) net.Send(MsgType.Next);
            else if (phase is Phase.Paused or Phase.RoundOver or Phase.GameOver && Input.BackPressed()) net.Send(MsgType.ToLobby);
            return;
        }

        if (_leavePrompt)
        {
            if (Input.BackPressed()) LeaveNetwork(null);
            else if (Input.ConfirmPressed()) _leavePrompt = false;
        }
        else if (Input.PausePressed()) _leavePrompt = true;
    }

    // ───────────────────────────── Events → trails ─────────────────────────────

    /// <summary>Turns the match's events into trail drawing, pop-ups and screenshots.</summary>
    private void DrainEvents()
    {
        var m = M;
        foreach (var e in m.Events)
        {
            switch (e)
            {
                case SegmentEvent s:
                    _pendingSegments.Add((s.A, s.B, m.Players[s.PlayerId].Color));
                    break;
                case ClearEvent:
                    if (Online) EnsureLayout();   // locally the layout was updated before the round started
                    _pendingSegments.Clear();
                    ClearTrails();
                    break;
                case PopupEvent p:
                    _popups.Add(new Popup(p.Type, p.Position, p.Tick));
                    break;
                case RoundEndEvent:
                    OnRoundEnd();
                    break;
            }
        }
        m.Events.Clear();
        _popups.RemoveAll(p => m.Tick - p.StartTick > PopupTicks || m.Tick < p.StartTick);
        FlushSegments();
    }

    // ───────────────────────────── Rendering ─────────────────────────────

    private void ClearTrails()
    {
        BeginTextureMode(_trails);
        ClearBackground(Background);
        EndTextureMode();
    }

    private void FlushSegments()
    {
        if (_pendingSegments.Count == 0) return;
        var s = _trailScale;
        BeginTextureMode(_trails);
        foreach (var (a, b, color) in _pendingSegments)
        {
            DrawLineEx(a * s, b * s, 2 * Radius * s, color);
            DrawCircleV(b * s, Radius * s, color);
        }
        EndTextureMode();
        _pendingSegments.Clear();
    }

    public void Draw()
    {
        var scale = ViewScale;
        var camera = new Camera2D
        {
            Zoom = scale,
            Offset = new Vector2((GetRenderWidth() - _logicalW * scale) / 2, (GetRenderHeight() - BaseHeight * scale) / 2),
        };

        BeginDrawing();
        ClearBackground(Background);
        BeginMode2D(camera);

        // Render textures are stored upside down, hence the negative source height.
        var tex = _trails.Texture;
        DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, -tex.Height),
            new Rectangle(FieldX, FieldY, _fieldW, FieldH), Vector2.Zero, 0, Color.White);
        DrawRectangleLinesEx(new Rectangle(FieldX - 4, FieldY - 4, _fieldW + 8, FieldH + 8), 4, Border);

        if (_screen == Screen.InGame)
        {
            DrawBoxes();
            DrawHeads();
            DrawPopups();
        }
        DrawPanel();
        DrawOverlay();

        EndMode2D();
        if (_screenshotPending) SaveScreenshot();
        EndDrawing();
    }

    // ───────────────────────────── Screenshots ─────────────────────────────

    private static readonly string ScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
            ? pictures
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AchtungDieKurve");

    /// <summary>
    /// Grabs the finished frame (round result visible) from the back buffer before it is shown.
    /// PNG encoding runs on a worker thread so the game doesn't stutter.
    /// </summary>
    private void SaveScreenshot()
    {
        _screenshotPending = false;
        var image = LoadImageFromScreen();
        var file = Path.Combine(ScreenshotDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_round{M.Round:00}.png");
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ScreenshotDir);
                Console.WriteLine(ExportImage(image, file) ? $"Screenshot saved: {file}" : $"Screenshot failed: {file}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Screenshot failed: {e.Message}");
            }
            finally
            {
                UnloadImage(image);
            }
        });
    }

    private static Vector2 ToScreen(Vector2 fieldPos) => fieldPos + new Vector2(FieldX, FieldY);

    private void DrawBoxes()
    {
        foreach (var box in M.Boxes)
            PowerUps.DrawBox(box with { Position = ToScreen(box.Position) }, M.Tick, PowerUpSpawner.LifetimeTicks);
    }

    private void DrawHeads()
    {
        var tick = M.Tick;
        foreach (var p in M.Active)
        {
            var screen = ToScreen(p.Position);
            if (M.Phase == Phase.Prepare)
            {
                DrawCircleV(screen, Radius + 1, p.Color);
                var tip = screen + p.Direction * 28;
                DrawLineEx(screen, tip, 2, p.Color);
                var side = new Vector2(-p.Direction.Y, p.Direction.X);
                DrawTriangle(tip + p.Direction * 8, tip + side * 5, tip - side * 5, p.Color);
                DrawTriangle(tip + p.Direction * 8, tip - side * 5, tip + side * 5, p.Color);
                continue;
            }
            if (!p.Alive) continue;

            if (p.StarTicks > 0)
                DrawCircleV(screen, Radius + 2.5f, ColorFromHSV(tick * 6 % 360, 1f, 1f));
            else if (p.GhostTicks > 0)
                DrawCircleV(screen, Radius + 1.5f, ColorAlpha(p.Color, tick / 10 % 2 == 0 ? 0.9f : 0.35f));
            else
                DrawCircleV(screen, Radius + 0.5f, HeadColor);

            // Show what is slowing / confusing this player right above the head.
            var x = screen.X - (p.SlowTicks > 0 && p.ReverseTicks > 0 ? 8 : 0);
            if (p.SlowTicks > 0) { PowerUps.DrawIcon(PowerUpType.Lightning, new Vector2(x, screen.Y - 16), 6, tick); x += 16; }
            if (p.ReverseTicks > 0) PowerUps.DrawIcon(PowerUpType.Banana, new Vector2(x, screen.Y - 16), 6, tick);
        }
    }

    private void DrawPopups()
    {
        foreach (var popup in _popups)
        {
            var age = M.Tick - popup.StartTick;
            var alpha = Math.Clamp(1.5f - age / (float)PopupTicks * 1.5f, 0f, 1f);
            var pos = ToScreen(popup.Position) - new Vector2(0, 24 + age * 0.15f);
            var text = PowerUps.Text(popup.Type);
            var color = ColorAlpha(PowerUps.TargetColor(PowerUps.Target(popup.Type)), alpha);
            const int size = 18;
            var w = MeasureText(text, size);
            var left = Math.Clamp(pos.X - (w + 26) / 2f, FieldX + 2, FieldX + _fieldW - w - 28);
            var top = Math.Clamp(pos.Y - 10, FieldY + 2, FieldY + FieldH - 22);
            DrawRectangleRounded(new Rectangle(left - 4, top - 3, w + 34, 26), 0.4f, 6, ColorAlpha(Color.Black, 0.6f * alpha));
            PowerUps.DrawIcon(popup.Type, new Vector2(left + 9, top + 10), 9, M.Tick);
            DrawText(text, (int)(left + 24), (int)top + 1, size, color);
        }
    }

    /// <summary>"1 / Q + Pad 1", "AI (Normal)", "on Kitchen-PC" ...</summary>
    private string ControlsText(Player p)
    {
        if (p.Kind == PlayerKind.Ai) return $"AI ({M.AiLevel})";
        if (_net is { } net && p.OwnerClient != net.ClientId)
            return $"on {net.Lobby.Client(p.OwnerClient)?.Name ?? "?"}";
        var keys = Input.KeyHint(p.KeySet);
        return Input.HasPad(p.HumanIndex) ? $"{keys}  +  Pad {p.HumanIndex + 1}" : keys;
    }

    private void DrawPanel()
    {
        var y = 24;
        DrawText("Achtung,", _panelX, y, 36, Border); y += 40;
        DrawText("die Kurve!", _panelX, y, 36, Border); y += 54;

        var inGame = _screen == Screen.InGame;
        DrawText($"Goal: {M.Goal} points", _panelX, y, 20, Color.LightGray); y += 22;
        DrawText("(2 points lead needed)", _panelX, y, 16, Color.Gray);
        if (inGame && M.Phase is Phase.Playing or Phase.RoundOver or Phase.Paused && M.RoundSpeed > 1.001f)
        {
            var speed = $"Speed x{M.RoundSpeed:0.00}";
            DrawText(speed, _logicalW - 24 - MeasureText(speed, 16), y, 16, new Color(255, 160, 60, 255));
        }
        y += 34;

        var list = inGame
            ? M.Active.OrderByDescending(p => p.Score)
            : M.Players.Where(p => p.Active);
        foreach (var p in list)
        {
            var dim = inGame && M.Phase is Phase.Playing or Phase.RoundOver && !p.Alive;
            var c = dim ? ColorAlpha(p.Color, 0.4f) : p.Color;
            DrawText(p.Name, _panelX, y, 26, c);
            var score = p.Score.ToString();
            if (inGame) DrawText(score, _logicalW - 24 - MeasureText(score, 26), y, 26, c);
            y += 28;

            DrawText(ControlsText(p), _panelX + 2, y, 16, Color.Gray);
            DrawEffectIcons(p, _logicalW - 30, y + 8);
            y += 28;
        }

        var host = _net?.IsHost == true;
        var hint = _screen switch
        {
            _ when _demo => "ESC|quit demo",
            Screen.MainMenu => "UP / DOWN|choose\nLEFT / RIGHT|change\nSPACE / A|select\nF11 / X|fullscreen\nESC|quit",
            Screen.LocalSetup => "UP / DOWN|choose\nLEFT / RIGHT|change\nSPACE / A|start\nESC / B|back",
            Screen.Join => "type|address\nENTER / A|connect\nCTRL+V|paste\nESC / B|back",
            Screen.Connecting => "ESC / B|cancel",
            Screen.Lobby when host => "UP / DOWN|choose\nLEFT / RIGHT|change\nSPACE / A|start\nESC / B|close game",
            Screen.Lobby => "ESC / B|leave",
            _ => M.Phase switch
            {
                Phase.Playing when Online && !host => "ESC / START|leave game\nF11 / X|fullscreen",
                Phase.Playing => "ESC / START|pause\nF11 / X|fullscreen",
                Phase.Paused when Online && !host => "",
                Phase.Paused => Online ? "SPACE / A|resume\nESC / B|lobby" : "SPACE / A|resume\nESC / B|menu",
                Phase.RoundOver when Online && !host => "ESC / START|leave game",
                Phase.RoundOver => Online ? "SPACE / A|next round\nESC / B|lobby" : "SPACE / A|next round\nESC / B|menu",
                Phase.GameOver when Online && !host => "ESC / START|leave game",
                Phase.GameOver => Online ? "SPACE / A|new game\nESC / B|lobby" : "SPACE / A|new game\nESC / B|menu",
                _ => "",
            },
        };
        var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hy = BaseHeight - 20 - lines.Length * 24;
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            DrawText(parts[0], _panelX, hy, 18, Color.LightGray);
            DrawText(parts[1], _panelX + 150, hy, 18, Color.Gray);
            hy += 24;
        }
    }

    /// <summary>Small icons of the player's active effects, drawn right-to-left from x.</summary>
    private void DrawEffectIcons(Player p, float x, float y)
    {
        if (!p.Alive || _screen != Screen.InGame) return;
        (int Ticks, PowerUpType Type)[] effects =
        [
            (p.BoostTicks, PowerUpType.Mushroom), (p.StarTicks, PowerUpType.Star), (p.GhostTicks, PowerUpType.Boo),
            (p.SlowTicks, PowerUpType.Lightning), (p.ReverseTicks, PowerUpType.Banana),
        ];
        foreach (var (ticks, type) in effects)
        {
            if (ticks <= 0) continue;
            PowerUps.DrawIcon(type, new Vector2(x, y), 8, M.Tick);
            x -= 22;
        }
    }

    private void DrawOverlay()
    {
        switch (_screen)
        {
            case Screen.MainMenu: DrawMainMenu(); break;
            case Screen.LocalSetup when !_demo: DrawLocalSetup(); break;
            case Screen.Join: DrawJoin(); break;
            case Screen.Connecting:
                CenterText("Connecting...", FieldH / 2 - 40, 40, Color.White);
                CenterText(_connectTarget, FieldH / 2 + 10, 24, Color.LightGray);
                break;
            case Screen.Lobby: DrawLobby(); break;
            case Screen.InGame: DrawGameOverlay(); break;
        }

        if (_messageTimer > 0 && _screen != Screen.InGame)
            CenterText(_message, FieldH - 150, 20, ErrorColor);
    }

    private void DrawGameOverlay()
    {
        var host = !Online || _net!.IsHost;
        switch (M.Phase)
        {
            case Phase.Prepare:
                CenterText(M.Round == 1 ? "Get ready..." : $"Round {M.Round}", FieldH / 2 - 14, 32, Color.White);
                break;
            case Phase.Paused:
                CenterText("Paused", FieldH / 2 - 20, 40, Color.White);
                if (!host) CenterText("The host paused the game", FieldH / 2 + 30, 20, Color.LightGray);
                break;
            case Phase.RoundOver:
                CenterText(M.RoundMessage, 40, 30, Color.White);
                if (!_demo) CenterText(host ? "Press SPACE or A for the next round" : "The next round starts soon...", 76, 20, Color.LightGray);
                break;
            case Phase.GameOver when M.Winner is not null:
                DrawRectangle(FieldX, FieldH / 2 - 80, _fieldW, 170, ColorAlpha(Color.Black, 0.75f));
                CenterText($"{M.Winner.Name} wins the game!", FieldH / 2 - 50, 48, M.Winner.Color);
                if (!_demo) CenterText(host ? "Press SPACE or A for a new game" : "Waiting for the host...", FieldH / 2 + 30, 24, Color.LightGray);
                break;
        }

        if (_leavePrompt)
        {
            DrawRectangle(FieldX, FieldH / 2 - 70, _fieldW, 150, ColorAlpha(Color.Black, 0.85f));
            CenterText("Leave the game?", FieldH / 2 - 50, 40, Color.White);
            CenterText("ESC / B: leave      SPACE / A: keep playing", FieldH / 2 + 10, 22, Color.LightGray);
        }
    }

    /// <summary>Draws menu rows (label + value, or a centred button) starting at y; returns the y below them.</summary>
    private int DrawMenuRows(int y, IReadOnlyList<MenuItem> items, int selected)
    {
        const int rowH = 38, size = 22;
        var labelX = FieldX + _fieldW / 2 - 250;
        var valueX = FieldX + _fieldW / 2 + 40;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isSelected = i == selected;
            if (isSelected)
                DrawRectangleRounded(new Rectangle(labelX - 20, y - 7, 540, rowH - 4), 0.3f, 6, Selection);

            if (item.Button)
            {
                var text = isSelected ? $"> {item.Label} <" : item.Label;
                var buttonSize = item.Label == "START" ? 28 : 24;
                DrawText(text, FieldX + (_fieldW - MeasureText(text, buttonSize)) / 2, y - (buttonSize - size) / 2, buttonSize,
                    isSelected ? Border : item.LabelColor);
            }
            else
            {
                DrawText(item.Label, labelX, y, size, item.LabelColor);
                DrawText(isSelected ? $"<  {item.Value}  >" : item.Value, valueX, y, size, isSelected ? Border : item.ValueColor);
            }
            y += rowH;
        }
        return y;
    }

    private void DrawTitle(string subtitle)
    {
        CenterText("Achtung, die Kurve!", 50, 56, Border);
        CenterText(subtitle, 116, 18, Color.LightGray);
    }

    private void DrawMainMenu()
    {
        DrawTitle("Steer, don't crash, outlive the others. Every crash gives the survivors a point.");
        var lp = _settings.LocalPlayers;
        MenuItem[] items =
        [
            new("Local game", "", Color.White, Color.White, Button: true),
            new("Host network game", "", Color.White, Color.White, Button: true),
            new("Join network game", "", Color.White, Color.White, Button: true),
            new("Players on this PC", lp == 1 ? "1 (network games)" : $"{lp} (network games)", Color.LightGray, Color.White),
            new("Resolution", Display.Describe(), Color.LightGray, Color.White),
            new("Quit", "", Color.Gray, Color.Gray, Button: true),
        ];
        var y = DrawMenuRows(FieldY + 190, items, _menuIndex);

        var info = (MainRow)_menuIndex switch
        {
            MainRow.Local => "Everyone plays on this PC: keyboard and controllers, plus AI opponents.",
            MainRow.Host => $"Other PCs join you over the network (port {_settings.Port}). You set up the game.",
            MainRow.Join => "Play on a game hosted by another PC or a dedicated server.",
            MainRow.LocalPlayers => $"Keys for network games: {string.Join(",  ", Input.KeySets.Take(lp).Select(k => k.Hint))}",
            _ => "",
        };
        CenterText(info, y - FieldY + 20, 18, Color.Gray);
        DrawPowerUpLegend(_settings.PowerUps);
    }

    private void DrawLocalSetup()
    {
        DrawTitle("Local game: choose who plays each colour.");
        var items = new List<MenuItem>();
        foreach (var p in _local.Players)
        {
            items.Add(new MenuItem(p.Name, p.Kind switch { PlayerKind.Human => "Human", PlayerKind.Ai => "AI", _ => "Off" },
                p.Active ? p.Color : ColorAlpha(p.Color, 0.45f), p.Active ? Color.White : Color.Gray));
        }
        items.Add(new MenuItem("AI difficulty", _settings.AiLevel.ToString(), Color.LightGray, Color.White));
        items.Add(new MenuItem("Power-ups", _settings.PowerUps ? "On" : "Off (classic)", Color.LightGray, Color.White));
        items.Add(new MenuItem("Resolution", Display.Describe(), Color.LightGray, Color.White));
        items.Add(new MenuItem("START", "", Color.White, Color.White, Button: true));
        DrawMenuRows(FieldY + 160, items, _menuIndex);
        DrawPowerUpLegend(_settings.PowerUps);
    }

    private void DrawJoin()
    {
        DrawTitle("Join a network game");
        var labelX = FieldX + _fieldW / 2 - 250;
        var y = FieldY + 200;

        var editing = (JoinRow)_menuIndex == JoinRow.Address;
        DrawText("Server address", labelX, y, 22, Color.LightGray);
        y += 34;
        var box = new Rectangle(labelX - 10, y - 8, 520, 44);
        DrawRectangleRounded(box, 0.2f, 6, editing ? Selection : new Color(30, 30, 30, 255));
        DrawRectangleRoundedLinesEx(box, 0.2f, 6, 2, editing ? Border : Color.Gray);
        var cursor = editing && (int)(GetTime() * 2) % 2 == 0 ? "_" : "";
        DrawText(_address + cursor, labelX, y, 28, Color.White);
        y += 70;

        DrawMenuRows(y, [new MenuItem("CONNECT", "", Color.White, Color.White, Button: true)], _menuIndex - 1);
        y += 60;

        CenterText("The IP address or name of the PC running the game, for example", y - FieldY, 18, Color.Gray);
        CenterText("192.168.1.20     kurve-server     kurve.example.org:7777", y - FieldY + 26, 18, Color.LightGray);
        var lp = _settings.LocalPlayers;
        CenterText($"Joining with {lp} player{(lp == 1 ? "" : "s")} on this PC (change in the main menu)", y - FieldY + 70, 18, Color.Gray);
    }

    private void DrawLobby()
    {
        if (_net is not { Welcomed: true } net)
        {
            CenterText("Joining...", FieldH / 2 - 20, 40, Color.White);
            return;
        }

        var lobby = net.Lobby;
        CenterText("Network lobby", 40, 48, Border);
        var host = lobby.Clients.FirstOrDefault(c => c.IsHost);
        CenterText(net.IsHost ? "You are the host: choose who plays each colour, then START."
            : $"Waiting for {host?.Name ?? "the host"} to start the game...", 100, 20, Color.LightGray);

        var items = new List<MenuItem>();
        foreach (var p in M.Players)
        {
            var s = lobby.Slots[p.Id];
            var value = s.Kind switch
            {
                PlayerKind.Ai => "AI",
                PlayerKind.Human when s.Owner == net.ClientId => $"You ({Input.KeyHint(s.KeySet)})",
                PlayerKind.Human => $"{lobby.Client(s.Owner)?.Name ?? "?"} #{s.KeySet + 1}",
                _ => "Off",
            };
            items.Add(new MenuItem(p.Name, value, p.Active ? p.Color : ColorAlpha(p.Color, 0.45f), p.Active ? Color.White : Color.Gray));
        }
        items.Add(new MenuItem("AI difficulty", lobby.AiLevel.ToString(), Color.LightGray, Color.White));
        items.Add(new MenuItem("Power-ups", lobby.PowerUps ? "On" : "Off (classic)", Color.LightGray, Color.White));
        if (net.IsHost) items.Add(new MenuItem("START", "", Color.White, Color.White, Button: true));
        var y = DrawMenuRows(FieldY + 150, items, net.IsHost ? _menuIndex : -1) + 16;

        // Who is connected, and where others can join.
        var left = FieldX + 40;
        DrawText("Connected PCs", left, y, 20, Color.LightGray);
        var ly = y + 28;
        foreach (var c in lobby.Clients)
        {
            var text = $"{c.Name}{(c.IsHost ? "  (host)" : "")}{(c.Id == net.ClientId ? "  - you" : "")}   {c.LocalPlayers} player{(c.LocalPlayers == 1 ? "" : "s")}";
            DrawText(text, left + 10, ly, 18, c.IsHost ? Border : Color.White);
            ly += 22;
        }

        var right = FieldX + _fieldW / 2 + 40;
        DrawText("Others join with", right, y, 20, Color.LightGray);
        var addresses = _server is not null && _hostAddresses.Length > 0 ? _hostAddresses : [net.Address];
        var ay = y + 28;
        foreach (var a in addresses.Take(4))
        {
            DrawText(a, right + 10, ay, 18, Color.White);
            ay += 22;
        }
    }

    private void DrawPowerUpLegend(bool enabled)
    {
        if (!enabled) return;
        (PowerUpType Type, string Text)[] legend =
        [
            (PowerUpType.Mushroom, "Speed"), (PowerUpType.Star, "Invincible"), (PowerUpType.Boo, "Invisible"),
            (PowerUpType.Lightning, "Slows others"), (PowerUpType.Banana, "Reverses others"), (PowerUpType.Bomb, "Clears board"),
        ];
        var ly = FieldY + FieldH - 70;
        CenterText("Drive through ? boxes to get a random item:", ly - FieldY - 34, 18, Color.Gray);
        var colW = Math.Min(170, (_fieldW - 40) / legend.Length);
        var lx = FieldX + (_fieldW - colW * legend.Length) / 2f;
        foreach (var (type, text) in legend)
        {
            var cx = lx + colW / 2f;
            PowerUps.DrawIcon(type, new Vector2(cx, ly + 4), 14, (int)(GetTime() * TickRate));
            var color = PowerUps.TargetColor(PowerUps.Target(type));
            DrawText(text, (int)(cx - MeasureText(text, 16) / 2f), (int)ly + 26, 16, color);
            lx += colW;
        }
    }

    private void CenterText(string text, int y, int size, Color color)
    {
        var w = MeasureText(text, size);
        DrawText(text, FieldX + (_fieldW - w) / 2, FieldY + y, size, color);
    }
}
