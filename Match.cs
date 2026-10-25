using System.Numerics;

namespace AchtungDieKurve;

public enum Phase { Prepare, Playing, Paused, RoundOver, GameOver }

/// <summary>Things that happened in the simulation which the renderer (or the network) needs to know about.</summary>
public abstract record MatchEvent;
public sealed record SegmentEvent(Vector2 A, Vector2 B, int PlayerId) : MatchEvent;
/// <summary>New round or Bob-omb: all trails are gone.</summary>
public sealed record ClearEvent : MatchEvent;
public sealed record PopupEvent(PowerUpType Type, Vector2 Position, int Tick) : MatchEvent;
public sealed record RoundEndEvent : MatchEvent;

/// <summary>
/// The game rules and simulation, without any drawing or input, so the same code runs locally,
/// on a dedicated server, and (as a mirror filled from snapshots) on network clients.
/// </summary>
public sealed class Match
{
    public const int TickRate = 120;
    public const float Dt = 1f / TickRate;
    public const float Radius = 2.6f;             // trail half-width
    public const int FieldHeight = 780;
    /// <summary>Network games use a fixed 16:9 field so every PC sees the same board.</summary>
    public const int OnlineFieldWidth = 1102;

    private const float Speed = 90f;              // logical px per second
    private const float TurnRate = 3.2f;          // rad per second at base speed
    private const float DeathSpeedUp = 1.15f;     // all remaining lines get faster per death
    private const float BoostFactor = 1.6f;       // Mushroom
    private const float SlowFactor = 0.6f;        // Lightning
    private const float PrepareSeconds = 1.5f;
    private const float SpawnMargin = 100f;
    private const float SpawnMinDistance = 80f;

    private static readonly float[] SampleAngles = [-1.05f, -0.52f, 0f, 0.52f, 1.05f];

    private readonly PowerUpSpawner _spawner = new();
    private readonly Random _rng = new();
    private float _accumulator;

    public Match(int fieldWidth, int fieldHeight) => Field = new Field(fieldWidth, fieldHeight);

    public Player[] Players { get; } = Player.CreateAll();
    public List<Player> Active { get; set; } = [];
    public Field Field { get; private set; }
    public int FieldWidth => Field.Width;
    public IReadOnlyList<PowerUpBox> Boxes => _spawner.Boxes;
    /// <summary>Drained by whoever renders or transmits the match.</summary>
    public List<MatchEvent> Events { get; } = [];

    public AiLevel AiLevel { get; set; } = AiLevel.Normal;
    public bool PowerUpsEnabled { get; set; } = true;

    public Phase Phase { get; set; } = Phase.Prepare;
    public int Tick { get; set; }
    public int Round { get; set; }
    public float RoundSpeed { get; set; } = 1f;
    public float PrepareTimer { get; set; }
    public int RoundPickups { get; private set; }
    public string RoundMessage { get; set; } = "";
    public Player? Winner { get; set; }

    public int Goal => 10 * (Math.Max(2, Players.Count(p => p.Active)) - 1);

    /// <summary>Network clients: show the server's boxes.</summary>
    public void SetBoxes(IEnumerable<PowerUpBox> boxes) => _spawner.SetBoxes(boxes);

    /// <summary>Local games: the field follows the window size (only call between rounds).</summary>
    public void Resize(int fieldWidth, int fieldHeight)
    {
        if (fieldWidth != Field.Width || fieldHeight != Field.Height) Field = new Field(fieldWidth, fieldHeight);
    }

    /// <summary>Applies the slot kinds (AI brains are created for AI slots).</summary>
    public void Configure(IReadOnlyList<PlayerKind> slots, AiLevel aiLevel, bool powerUps)
    {
        AiLevel = aiLevel;
        PowerUpsEnabled = powerUps;
        var humanIndex = 0;
        foreach (var p in Players)
        {
            p.Kind = slots[p.Id];
            p.Brain = p.Kind == PlayerKind.Ai ? new AiBrain(aiLevel, _rng) : null;
            p.HumanIndex = p.Kind == PlayerKind.Human ? humanIndex++ : -1;
        }
    }

    /// <summary>Hands a player over to the computer (e.g. its PC left a network game).</summary>
    public void MakeAi(Player p)
    {
        p.Kind = PlayerKind.Ai;
        p.Brain = new AiBrain(AiLevel, _rng);
        p.HumanIndex = p.KeySet = p.OwnerClient = -1;
    }

    public void NewGame()
    {
        Active = Players.Where(p => p.Active).ToList();
        foreach (var p in Players) p.Score = 0;
        Winner = null;
        Round = 0;
        StartRound();
    }

    public void StartRound()
    {
        Field.Clear();
        Events.Add(new ClearEvent());
        Tick = 0;
        RoundSpeed = 1f;
        Round++;
        RoundPickups = 0;
        RoundMessage = "";
        _spawner.Reset(Tick, _rng);

        var spawned = new List<Vector2>();
        foreach (var p in Active)
        {
            Vector2 pos;
            var attempts = 0;
            do
            {
                pos = new Vector2(
                    SpawnMargin + _rng.NextSingle() * (Field.Width - 2 * SpawnMargin),
                    SpawnMargin + _rng.NextSingle() * (Field.Height - 2 * SpawnMargin));
            } while (spawned.Any(s => Vector2.Distance(s, pos) < SpawnMinDistance) && ++attempts < 100);

            spawned.Add(pos);
            p.Position = p.PreviousPosition = pos;
            p.Angle = _rng.NextSingle() * MathF.Tau;
            p.Alive = true;
            p.GapTicksLeft = 0;
            p.TicksUntilGap = RandomGapInterval();
            p.ResetEffects();
        }

        PrepareTimer = PrepareSeconds;
        Phase = Phase.Prepare;
    }

    public void Pause()
    {
        if (Phase == Phase.Playing) Phase = Phase.Paused;
    }

    public void Resume()
    {
        if (Phase != Phase.Paused) return;
        Phase = Phase.Playing;
        _accumulator = 0;
    }

    /// <summary>Advances the countdown / simulation by real time; humans are steered by <paramref name="humanSteer"/>.</summary>
    /// <param name="maxFrame">Longer frames (e.g. a stalled window) are cut to this, so the game doesn't jump.</param>
    public void Advance(float frameTime, Func<Player, float> humanSteer, float maxFrame = 0.25f)
    {
        switch (Phase)
        {
            case Phase.Prepare:
                PrepareTimer -= frameTime;
                if (PrepareTimer <= 0) { Phase = Phase.Playing; _accumulator = 0; }
                break;

            case Phase.Playing:
            case Phase.RoundOver:   // the survivor keeps driving until the next round is started
                _accumulator += MathF.Min(frameTime, maxFrame);
                var phase = Phase;
                while (_accumulator >= Dt)
                {
                    _accumulator -= Dt;
                    Step(humanSteer, scoring: phase == Phase.Playing);
                    if (Phase != phase) break;
                }
                break;
        }
    }

    private void Step(Func<Player, float> humanSteer, bool scoring)
    {
        Tick++;
        var died = new List<Player>();

        foreach (var p in Active)
        {
            if (!p.Alive) continue;

            var ratio = RoundSpeed * (p.BoostTicks > 0 ? BoostFactor : 1f) * (p.SlowTicks > 0 ? SlowFactor : 1f);
            var step = Speed * Dt * ratio;
            var turn = TurnRate * Dt * MathF.Sqrt(ratio);   // faster lines turn a bit faster too

            var steer = p.Brain is { } brain
                ? brain.Decide(p, new AiWorld(Field, Tick, step, turn, ratio, _spawner.Boxes))
                : Math.Clamp(humanSteer(p), -1f, 1f);
            if (p.ReverseTicks > 0) steer = -steer;

            p.Angle += steer * turn;
            p.PreviousPosition = p.Position;
            p.Position += p.Direction * step;

            var wrapped = false;
            if (p.StarTicks > 0)
            {
                var w = new Vector2((p.Position.X + Field.Width) % Field.Width, (p.Position.Y + Field.Height) % Field.Height);
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
                Field.StampSegment(from, p.Position, Radius, p.Id, Tick);
                Events.Add(new SegmentEvent(from, p.Position, p.Id));
            }

            if (PowerUpsEnabled && _spawner.TryPickup(p)) ApplyPowerUp(p, PowerUps.Roll(_rng));
        }

        // Resolve deaths after everyone moved, so simultaneous crashes are fair.
        foreach (var p in died)
        {
            p.Alive = false;
            RoundSpeed *= DeathSpeedUp;
        }

        if (PowerUpsEnabled) _spawner.Update(Tick, Field, Active.Where(p => p.Alive), _rng);

        if (scoring && died.Count > 0)
        {
            foreach (var p in Active.Where(p => p.Alive)) p.Score += died.Count;
            CheckRoundEnd();
        }
    }

    private void ApplyPowerUp(Player p, PowerUpType type)
    {
        var others = Active.Where(o => o.Alive && o != p);
        switch (type)
        {
            case PowerUpType.Mushroom: p.BoostTicks = 4 * TickRate; break;
            case PowerUpType.Star: p.StarTicks = 5 * TickRate; break;
            case PowerUpType.Boo: p.GhostTicks = 3 * TickRate; break;
            case PowerUpType.Lightning: foreach (var o in others) o.SlowTicks = 4 * TickRate; break;
            case PowerUpType.Banana: foreach (var o in others) o.ReverseTicks = 3 * TickRate; break;
            case PowerUpType.Bomb:
                Field.Clear();
                Events.RemoveAll(e => e is SegmentEvent);
                Events.Add(new ClearEvent());
                break;
        }
        Events.Add(new PopupEvent(type, p.Position, Tick));
        RoundPickups++;
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
        var alive = Active.Where(p => p.Alive).ToList();
        if (alive.Count > 1) return;

        RoundMessage = alive.Count == 1 ? $"{alive[0].Name} wins the round!" : "Nobody survived!";
        Events.Add(new RoundEndEvent());

        var ranked = Active.OrderByDescending(p => p.Score).ToList();
        if (ranked[0].Score >= Goal && ranked[0].Score - ranked[1].Score >= 2)
        {
            Winner = ranked[0];
            Phase = Phase.GameOver;
            return;
        }
        Phase = Phase.RoundOver;
    }

    private bool Collides(Player p)
    {
        var dir = p.Direction;
        foreach (var a in SampleAngles)
        {
            var (s, c) = MathF.SinCos(a);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var sample = p.Position + d * (Radius + 1.5f);
            int x = (int)MathF.Floor(sample.X), y = (int)MathF.Floor(sample.Y);

            if (!Field.InBounds(x, y))
            {
                if (p.StarTicks > 0) continue;   // Star wraps around the walls
                return true;
            }
            if (!p.PassesTrails && Field.IsTrail(x, y, p.Id, Tick)) return true;
        }
        return false;
    }
}
