using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace AchtungDieKurve;

/// <summary>
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
    private const int FieldH = BaseHeight - 2 * FieldY;
    private const int PanelWidth = 310;

    private const int TickRate = 120;
    private const float Dt = 1f / TickRate;
    private const float Speed = 90f;              // logical px per second
    private const float TurnRate = 3.2f;          // rad per second at base speed
    private const float Radius = 2.6f;            // trail half-width
    private const float DeathSpeedUp = 1.15f;     // all remaining lines get faster per death
    private const float BoostFactor = 1.6f;       // Mushroom
    private const float SlowFactor = 0.6f;        // Lightning
    private const float PrepareSeconds = 1.5f;
    private const float SpawnMargin = 100f;
    private const float SpawnMinDistance = 80f;
    private const int PopupTicks = TickRate * 3 / 2;

    private static readonly Color Background = Color.Black;
    private static readonly Color Border = new(230, 210, 40, 255);
    private static readonly Color HeadColor = new(255, 235, 60, 255);
    private static readonly float[] SampleAngles = [-1.05f, -0.52f, 0f, 0.52f, 1.05f];

    private enum State { Title, Prepare, Playing, RoundOver, Paused, GameOver }

    private enum MenuRow { Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, AiLevel, PowerUps, Resolution, Start }
    private static readonly int MenuRowCount = Enum.GetValues<MenuRow>().Length;

    private sealed record Popup(PowerUpType Type, Vector2 Position, int StartTick);

    private readonly Player[] _players =
    [
        new(0, "Fred",     new Color(235, 50, 50, 255),   KeyboardKey.One,  KeyboardKey.Q,     "1 / Q"),
        new(1, "Greenlee", new Color(60, 200, 60, 255),   KeyboardKey.M,    KeyboardKey.Comma, "M / ,"),
        new(2, "Bluebell", new Color(60, 140, 255, 255),  KeyboardKey.Left, KeyboardKey.Right, "Left / Right"),
        new(3, "Pinkney",  new Color(255, 110, 200, 255), KeyboardKey.Null, KeyboardKey.Null,  ""),
        new(4, "Willem",   new Color(255, 150, 40, 255),  KeyboardKey.Null, KeyboardKey.Null,  ""),
        new(5, "Greydon",  new Color(175, 175, 175, 255), KeyboardKey.Null, KeyboardKey.Null,  ""),
    ];

    private readonly Settings _settings;
    private readonly bool _demo;
    private readonly int _demoRounds;
    private readonly float _timeScale;
    private readonly bool _screenshots;
    private readonly List<(Vector2 A, Vector2 B, Color Color)> _pendingSegments = [];
    private readonly PowerUpSpawner _spawner = new();
    private readonly List<Popup> _popups = [];
    private readonly Random _rng = new();

    // Layout, (re)built by EnsureLayout().
    private int _logicalW;
    private int _fieldW;
    private int _panelX;
    private Field _field = new(1, 1);
    private RenderTexture2D _trails;
    private float _trailScale;
    private bool _hasTrails;

    private State _state = State.Title;
    private List<Player> _active = [];
    private float _accumulator;
    private float _prepareTimer;
    private float _autoTimer;
    private int _tick;
    private float _roundSpeed = 1f;
    private int _round;
    private int _roundPickups;
    private readonly List<float> _roundDurations = [];
    private string _roundMessage = "";
    private Player? _winner;
    private int _menuIndex = (int)MenuRow.Start;
    private bool _screenshotPending;
    private string _menuMessage = "";
    private float _menuMessageTimer;

    public bool QuitRequested { get; private set; }

    private int Goal => 10 * (Math.Max(2, _players.Count(p => p.Active)) - 1);

    /// <param name="demoLevel">If set, all six slots are AI of this level and the game runs by itself.</param>
    /// <param name="demoRounds">Demo only: quit after this many rounds and print statistics (0 = endless).</param>
    /// <param name="timeScale">Simulation speed multiplier (testing).</param>
    /// <param name="screenshots">Save a screenshot after every round (default: on, except in demo mode).</param>
    public Game(Settings settings, AiLevel? demoLevel = null, int demoRounds = 0, float timeScale = 1f, bool? screenshots = null)
    {
        _settings = settings;
        _timeScale = timeScale;
        _screenshots = screenshots ?? demoLevel is null;
        if (demoLevel is { } level)
        {
            _demo = true;
            _demoRounds = demoRounds;
            _settings = new Settings { Slots = Enumerable.Repeat(PlayerKind.Ai, Settings.SlotCount).ToArray(), AiLevel = level };
        }
        ApplySettings();
        EnsureLayout();
    }

    public void Dispose()
    {
        if (_hasTrails) UnloadRenderTexture(_trails);
    }

    private void ApplySettings()
    {
        var humanIndex = 0;
        foreach (var p in _players)
        {
            p.Kind = _settings.Slots[p.Id];
            p.Brain = p.Kind == PlayerKind.Ai ? new AiBrain(_settings.AiLevel, _rng) : null;
            p.HumanIndex = p.Kind == PlayerKind.Human ? humanIndex++ : -1;
        }
    }

    // ───────────────────────────── Layout / scaling ─────────────────────────────

    private static int DesiredLogicalWidth() =>
        Math.Max(BaseWidth, (int)(BaseHeight * GetRenderWidth() / (float)Math.Max(1, GetRenderHeight())));

    private float ViewScale => MathF.Min(GetRenderWidth() / (float)_logicalW, GetRenderHeight() / (float)BaseHeight);

    /// <summary>
    /// Rebuilds field size, collision grid and trail texture if the window changed.
    /// Only called between rounds, so a running round is never disturbed (it is just rescaled).
    /// </summary>
    private void EnsureLayout()
    {
        var logicalW = DesiredLogicalWidth();
        if (logicalW != _logicalW)
        {
            _logicalW = logicalW;
            _fieldW = _logicalW - FieldX - PanelWidth;
            _panelX = FieldX + _fieldW + 20;
            _field = new Field(_fieldW, FieldH);
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

        if (_demo && IsKeyPressed(KeyboardKey.Escape)) { QuitRequested = true; return; }
        if (Input.FullscreenPressed()) Display.ToggleFullscreen();

        switch (_state)
        {
            case State.Title:
                EnsureLayout();
                if (_demo) NewGame();
                else UpdateMenu(frameTime);
                break;

            case State.Prepare:
                _prepareTimer -= frameTime;
                if (_prepareTimer <= 0) { _state = State.Playing; _accumulator = 0; }
                else if (Input.PausePressed()) _state = State.Title;
                break;

            case State.Playing:
                if (!_demo && Input.PausePressed()) { _state = State.Paused; break; }
                Simulate(frameTime, scoring: true);
                break;

            case State.RoundOver:
                // The survivor keeps driving until the next round is started.
                Simulate(frameTime, scoring: false);
                if (_demo)
                {
                    if ((_autoTimer -= frameTime) <= 0) StartRound();
                }
                else if (Input.ConfirmPressed()) StartRound();
                else if (Input.BackPressed()) _state = State.Title;
                break;

            case State.Paused:
                if (Input.ConfirmPressed()) { _state = State.Playing; _accumulator = 0; }
                else if (Input.BackPressed()) _state = State.Title;
                break;

            case State.GameOver:
                if (_demo)
                {
                    if ((_autoTimer -= frameTime) <= 0) NewGame();
                }
                else if (Input.ConfirmPressed()) NewGame();
                else if (Input.BackPressed()) _state = State.Title;
                break;
        }
    }

    private void UpdateMenu(float frameTime)
    {
        _menuMessageTimer -= frameTime;
        _menuIndex = (_menuIndex + Input.MenuDy + MenuRowCount) % MenuRowCount;
        if (Input.MenuDx != 0) ChangeMenuValue((MenuRow)_menuIndex, Input.MenuDx);

        if (Input.ConfirmPressed())
        {
            if (_players.Count(p => p.Active) < 2)
            {
                _menuMessage = "At least 2 players needed - set someone to Human or AI";
                _menuMessageTimer = 3;
            }
            else
            {
                _settings.Save();
                NewGame();
            }
        }
        // Only the keyboard quits from the title, so a stray B press on a pad can't close the game.
        else if (IsKeyPressed(KeyboardKey.Escape)) QuitRequested = true;
    }

    private void ChangeMenuValue(MenuRow row, int delta)
    {
        switch (row)
        {
            case <= MenuRow.Slot5:
            {
                var slot = (int)row;
                PlayerKind[] options = slot < Settings.HumanSlots
                    ? [PlayerKind.Human, PlayerKind.Ai, PlayerKind.Off]
                    : [PlayerKind.Ai, PlayerKind.Off];
                var idx = Math.Max(0, Array.IndexOf(options, _settings.Slots[slot]));
                _settings.Slots[slot] = options[(idx + delta + options.Length) % options.Length];
                break;
            }
            case MenuRow.AiLevel:
                _settings.AiLevel = (AiLevel)(((int)_settings.AiLevel + delta + 3) % 3);
                break;
            case MenuRow.PowerUps:
                _settings.PowerUps = !_settings.PowerUps;
                break;
            case MenuRow.Resolution:
                Display.CycleResolution();
                break;
        }
        ApplySettings();
    }

    private void NewGame()
    {
        ApplySettings();
        _active = _players.Where(p => p.Active).ToList();
        foreach (var p in _players) p.Score = 0;
        _winner = null;
        StartRound();
    }

    private void StartRound()
    {
        EnsureLayout();
        ClearTrails();
        _field.Clear();
        _pendingSegments.Clear();
        _popups.Clear();
        _tick = 0;
        _roundSpeed = 1f;
        _round++;
        _roundPickups = 0;
        _roundMessage = "";
        _spawner.Reset(_tick, _rng);

        var spawned = new List<Vector2>();
        foreach (var p in _active)
        {
            Vector2 pos;
            var attempts = 0;
            do
            {
                pos = new Vector2(
                    SpawnMargin + _rng.NextSingle() * (_fieldW - 2 * SpawnMargin),
                    SpawnMargin + _rng.NextSingle() * (FieldH - 2 * SpawnMargin));
            } while (spawned.Any(s => Vector2.Distance(s, pos) < SpawnMinDistance) && ++attempts < 100);

            spawned.Add(pos);
            p.Position = p.PreviousPosition = pos;
            p.Angle = _rng.NextSingle() * MathF.Tau;
            p.Alive = true;
            p.GapTicksLeft = 0;
            p.TicksUntilGap = RandomGapInterval();
            p.ResetEffects();
        }

        _prepareTimer = PrepareSeconds;
        _state = State.Prepare;
    }

    private void Simulate(float frameTime, bool scoring)
    {
        _accumulator += MathF.Min(frameTime, 0.25f * _timeScale);
        while (_accumulator >= Dt)
        {
            _accumulator -= Dt;
            Tick(scoring);
            if (_state is not (State.Playing or State.RoundOver)) break;
        }
        FlushSegments();
    }

    private void Tick(bool scoring)
    {
        _tick++;
        var died = new List<Player>();

        foreach (var p in _active)
        {
            if (!p.Alive) continue;

            var ratio = _roundSpeed * (p.BoostTicks > 0 ? BoostFactor : 1f) * (p.SlowTicks > 0 ? SlowFactor : 1f);
            var step = Speed * Dt * ratio;
            var turn = TurnRate * Dt * MathF.Sqrt(ratio);   // faster lines turn a bit faster too

            var steer = p.Brain is { } brain
                ? brain.Decide(p, new AiWorld(_field, _tick, step, turn, ratio, _spawner.Boxes))
                : Input.Steering(p);
            if (p.ReverseTicks > 0) steer = -steer;

            p.Angle += steer * turn;
            p.PreviousPosition = p.Position;
            p.Position += p.Direction * step;

            var wrapped = false;
            if (p.StarTicks > 0)
            {
                var w = new Vector2((p.Position.X + _fieldW) % _fieldW, (p.Position.Y + FieldH) % FieldH);
                wrapped = w != p.Position;
                p.Position = w;
            }

            UpdateGap(p);
            p.TickEffects();

            if (Collides(p))
            {
                died.Add(p);
                continue;
            }

            if (p.LeavesTrail)
            {
                var from = wrapped ? p.Position : p.PreviousPosition;
                _field.StampSegment(from, p.Position, Radius, p.Id, _tick);
                _pendingSegments.Add((from, p.Position, p.Color));
            }

            if (_settings.PowerUps && _spawner.TryPickup(p)) ApplyPowerUp(p, PowerUps.Roll(_rng));
        }

        // Resolve deaths after everyone moved, so simultaneous crashes are fair.
        foreach (var p in died)
        {
            p.Alive = false;
            _roundSpeed *= DeathSpeedUp;
        }

        if (_settings.PowerUps) _spawner.Update(_tick, _field, _active.Where(p => p.Alive), _rng);
        _popups.RemoveAll(p => _tick - p.StartTick > PopupTicks);

        if (scoring && died.Count > 0)
        {
            foreach (var p in _active.Where(p => p.Alive)) p.Score += died.Count;
            CheckRoundEnd();
        }
    }

    private void ApplyPowerUp(Player p, PowerUpType type)
    {
        var others = _active.Where(o => o.Alive && o != p);
        switch (type)
        {
            case PowerUpType.Mushroom: p.BoostTicks = 4 * TickRate; break;
            case PowerUpType.Star: p.StarTicks = 5 * TickRate; break;
            case PowerUpType.Boo: p.GhostTicks = 3 * TickRate; break;
            case PowerUpType.Lightning: foreach (var o in others) o.SlowTicks = 4 * TickRate; break;
            case PowerUpType.Banana: foreach (var o in others) o.ReverseTicks = 3 * TickRate; break;
            case PowerUpType.Bomb:
                _field.Clear();
                _pendingSegments.Clear();
                ClearTrails();
                break;
        }
        _popups.Add(new Popup(type, p.Position, _tick));
        _roundPickups++;
    }

    private void UpdateGap(Player p)
    {
        if (p.GapTicksLeft > 0)
        {
            if (--p.GapTicksLeft == 0) p.TicksUntilGap = RandomGapInterval();
        }
        else if (--p.TicksUntilGap <= 0)
        {
            p.GapTicksLeft = (int)(TickRate * (0.17f + _rng.NextSingle() * 0.08f));
        }
    }

    private int RandomGapInterval() => (int)(TickRate * (1.2f + _rng.NextSingle() * 2.3f));

    private void CheckRoundEnd()
    {
        var alive = _active.Where(p => p.Alive).ToList();
        if (alive.Count > 1) return;

        _roundMessage = alive.Count == 1 ? $"{alive[0].Name} wins the round!" : "Nobody survived!";
        LogRound(alive.FirstOrDefault());
        if (_screenshots) _screenshotPending = true;

        var ranked = _active.OrderByDescending(p => p.Score).ToList();
        if (ranked[0].Score >= Goal && ranked[0].Score - ranked[1].Score >= 2)
        {
            _winner = ranked[0];
            _state = State.GameOver;
            _autoTimer = 3;
            if (_demo) Console.WriteLine($"=== {_winner.Name} wins the game: " +
                                         string.Join(", ", ranked.Select(p => $"{p.Name} {p.Score}")));
            return;
        }

        _state = State.RoundOver;
        _autoTimer = 2;
    }

    private void LogRound(Player? survivor)
    {
        if (!_demo || QuitRequested) return;
        var seconds = _tick / (float)TickRate;
        _roundDurations.Add(seconds);
        Console.WriteLine($"Round {_round}: {seconds,5:0.0}s  final speed x{_roundSpeed:0.00}  " +
                          $"power-ups {_roundPickups}  winner {survivor?.Name ?? "-"}");

        if (_demoRounds > 0 && _roundDurations.Count >= _demoRounds)
        {
            Console.WriteLine($"Average round length ({_settings.AiLevel}, {_roundDurations.Count} rounds): " +
                              $"{_roundDurations.Average():0.0}s");
            QuitRequested = true;
        }
    }

    // ───────────────────────────── Collision ─────────────────────────────

    private bool Collides(Player p)
    {
        var dir = p.Direction;
        foreach (var a in SampleAngles)
        {
            var (s, c) = MathF.SinCos(a);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var sample = p.Position + d * (Radius + 1.5f);
            int x = (int)MathF.Floor(sample.X), y = (int)MathF.Floor(sample.Y);

            if (!_field.InBounds(x, y))
            {
                if (p.StarTicks > 0) continue;   // Star wraps around the walls
                return true;
            }
            if (!p.PassesTrails && _field.IsTrail(x, y, p.Id, _tick)) return true;
        }
        return false;
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

        if (_state != State.Title)
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
        var file = Path.Combine(ScreenshotDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_round{_round:00}.png");
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
        foreach (var box in _spawner.Boxes)
            PowerUps.DrawBox(box with { Position = ToScreen(box.Position) }, _tick, PowerUpSpawner.LifetimeTicks);
    }

    private void DrawHeads()
    {
        foreach (var p in _active)
        {
            var screen = ToScreen(p.Position);
            if (_state == State.Prepare)
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
                DrawCircleV(screen, Radius + 2.5f, ColorFromHSV(_tick * 6 % 360, 1f, 1f));
            else if (p.GhostTicks > 0)
                DrawCircleV(screen, Radius + 1.5f, ColorAlpha(p.Color, _tick / 10 % 2 == 0 ? 0.9f : 0.35f));
            else
                DrawCircleV(screen, Radius + 0.5f, HeadColor);

            // Show what is slowing / confusing this player right above the head.
            var x = screen.X - (p.SlowTicks > 0 && p.ReverseTicks > 0 ? 8 : 0);
            if (p.SlowTicks > 0) { PowerUps.DrawIcon(PowerUpType.Lightning, new Vector2(x, screen.Y - 16), 6, _tick); x += 16; }
            if (p.ReverseTicks > 0) PowerUps.DrawIcon(PowerUpType.Banana, new Vector2(x, screen.Y - 16), 6, _tick);
        }
    }

    private void DrawPopups()
    {
        foreach (var popup in _popups)
        {
            var age = _tick - popup.StartTick;
            var alpha = Math.Clamp(1.5f - age / (float)PopupTicks * 1.5f, 0f, 1f);
            var pos = ToScreen(popup.Position) - new Vector2(0, 24 + age * 0.15f);
            var text = PowerUps.Text(popup.Type);
            var color = ColorAlpha(PowerUps.TargetColor(PowerUps.Target(popup.Type)), alpha);
            const int size = 18;
            var w = MeasureText(text, size);
            var left = Math.Clamp(pos.X - (w + 26) / 2f, FieldX + 2, FieldX + _fieldW - w - 28);
            var top = Math.Clamp(pos.Y - 10, FieldY + 2, FieldY + FieldH - 22);
            DrawRectangleRounded(new Rectangle(left - 4, top - 3, w + 34, 26), 0.4f, 6, ColorAlpha(Color.Black, 0.6f * alpha));
            PowerUps.DrawIcon(popup.Type, new Vector2(left + 9, top + 10), 9, _tick);
            DrawText(text, (int)(left + 24), (int)top + 1, size, color);
        }
    }

    private void DrawPanel()
    {
        var y = 24;
        DrawText("Achtung,", _panelX, y, 36, Border); y += 40;
        DrawText("die Kurve!", _panelX, y, 36, Border); y += 54;

        DrawText($"Goal: {Goal} points", _panelX, y, 20, Color.LightGray); y += 22;
        DrawText("(2 points lead needed)", _panelX, y, 16, Color.Gray);
        if (_state is State.Playing or State.RoundOver or State.Paused && _roundSpeed > 1.001f)
        {
            var speed = $"Speed x{_roundSpeed:0.00}";
            DrawText(speed, _logicalW - 24 - MeasureText(speed, 16), y, 16, new Color(255, 160, 60, 255));
        }
        y += 34;

        var list = _state == State.Title
            ? _players.Where(p => p.Active)
            : _active.OrderByDescending(p => p.Score);
        foreach (var p in list)
        {
            var dim = _state is State.Playing or State.RoundOver && !p.Alive;
            var c = dim ? ColorAlpha(p.Color, 0.4f) : p.Color;
            DrawText(p.Name, _panelX, y, 26, c);
            var score = p.Score.ToString();
            DrawText(score, _logicalW - 24 - MeasureText(score, 26), y, 26, c);
            y += 28;

            var controls = p.Kind switch
            {
                PlayerKind.Ai => $"AI ({_settings.AiLevel})",
                _ when Input.HasPad(p) => $"{p.KeyHint}  +  Pad {p.HumanIndex + 1}",
                _ => p.KeyHint,
            };
            DrawText(controls, _panelX + 2, y, 16, Color.Gray);
            DrawEffectIcons(p, _logicalW - 30, y + 8);
            y += 28;
        }

        var hint = _state switch
        {
            _ when _demo => "ESC|quit demo",
            State.Title => "UP / DOWN|choose\nLEFT / RIGHT|change\nSPACE / A|start\nF11 / X|fullscreen\nESC|quit",
            State.Playing => "ESC / START|pause\nF11 / X|fullscreen",
            State.Paused => "SPACE / A|resume\nESC / B|menu",
            State.RoundOver => "SPACE / A|next round\nESC / B|menu",
            State.GameOver => "SPACE / A|new game\nESC / B|menu",
            _ => "",
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
        if (!p.Alive || _state == State.Title) return;
        (int Ticks, PowerUpType Type)[] effects =
        [
            (p.BoostTicks, PowerUpType.Mushroom), (p.StarTicks, PowerUpType.Star), (p.GhostTicks, PowerUpType.Boo),
            (p.SlowTicks, PowerUpType.Lightning), (p.ReverseTicks, PowerUpType.Banana),
        ];
        foreach (var (ticks, type) in effects)
        {
            if (ticks <= 0) continue;
            PowerUps.DrawIcon(type, new Vector2(x, y), 8, _tick);
            x -= 22;
        }
    }

    private void DrawOverlay()
    {
        switch (_state)
        {
            case State.Title when !_demo:
                DrawMenu();
                break;
            case State.Prepare:
                CenterText(_round == 1 ? "Get ready..." : $"Round {_round}", FieldH / 2 - 14, 32, Color.White);
                break;
            case State.Paused:
                CenterText("Paused", FieldH / 2 - 20, 40, Color.White);
                break;
            case State.RoundOver:
                CenterText(_roundMessage, 40, 30, Color.White);
                if (!_demo) CenterText("Press SPACE or A for the next round", 76, 20, Color.LightGray);
                break;
            case State.GameOver when _winner is not null:
                DrawRectangle(FieldX, FieldH / 2 - 80, _fieldW, 170, ColorAlpha(Color.Black, 0.75f));
                CenterText($"{_winner.Name} wins the game!", FieldH / 2 - 50, 48, _winner.Color);
                if (!_demo) CenterText("Press SPACE or A for a new game", FieldH / 2 + 30, 24, Color.LightGray);
                break;
        }
    }

    private void DrawMenu()
    {
        CenterText("Achtung, die Kurve!", 50, 56, Border);
        CenterText("Steer, don't crash, outlive the others. Every crash gives the survivors a point.", 116, 18, Color.LightGray);

        const int rowH = 38, size = 22;
        var labelX = FieldX + _fieldW / 2 - 250;
        var valueX = FieldX + _fieldW / 2 + 40;
        var y = FieldY + 160;

        for (var i = 0; i < MenuRowCount; i++)
        {
            var row = (MenuRow)i;
            var selected = i == _menuIndex;
            if (selected)
                DrawRectangleRounded(new Rectangle(labelX - 20, y - 7, 540, rowH - 4), 0.3f, 6, new Color(60, 60, 30, 255));

            string label, value;
            var labelColor = Color.LightGray;
            var valueColor = Color.White;
            switch (row)
            {
                case <= MenuRow.Slot5:
                    var p = _players[i];
                    label = p.Name;
                    labelColor = p.Active ? p.Color : ColorAlpha(p.Color, 0.45f);
                    value = p.Kind switch { PlayerKind.Human => "Human", PlayerKind.Ai => "AI", _ => "Off" };
                    if (!p.Active) valueColor = Color.Gray;
                    break;
                case MenuRow.AiLevel: label = "AI difficulty"; value = _settings.AiLevel.ToString(); break;
                case MenuRow.PowerUps: label = "Power-ups"; value = _settings.PowerUps ? "On" : "Off (classic)"; break;
                case MenuRow.Resolution: label = "Resolution"; value = Display.Describe(); break;
                default:
                    label = ""; value = "";
                    var start = selected ? "> START <" : "START";
                    DrawText(start, FieldX + (_fieldW - MeasureText(start, 28)) / 2, y - 3, 28, selected ? Border : Color.White);
                    break;
            }

            if (row != MenuRow.Start)
            {
                DrawText(label, labelX, y, size, labelColor);
                DrawText(selected ? $"<  {value}  >" : value, valueX, y, size, selected ? Border : valueColor);
            }
            y += rowH;
        }

        if (_menuMessageTimer > 0) CenterText(_menuMessage, y - FieldY + 6, 20, new Color(255, 110, 110, 255));

        // Power-up legend
        if (!_settings.PowerUps) return;
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
