using System.Numerics;
using Raylib_cs;

namespace AchtungDieKurve;

public enum PlayerKind { Off, Human, Ai }

public sealed class Player(int id, string name, Color color, KeyboardKey left, KeyboardKey right, string keyHint)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public Color Color { get; } = color;
    public KeyboardKey LeftKey { get; } = left;
    public KeyboardKey RightKey { get; } = right;
    public string KeyHint { get; } = keyHint;
    public bool HasKeys => LeftKey != KeyboardKey.Null;

    public PlayerKind Kind;
    /// <summary>Position among the human players (0-based); the N-th controller goes to the N-th human.</summary>
    public int HumanIndex = -1;
    public AiBrain? Brain;
    public bool Active => Kind != PlayerKind.Off;

    public Vector2 Position;
    public Vector2 PreviousPosition;
    public float Angle;
    public bool Alive;
    public int Score;

    /// <summary>Remaining ticks of the current gap (0 = drawing).</summary>
    public int GapTicksLeft;
    /// <summary>Ticks until the next gap starts.</summary>
    public int TicksUntilGap;

    // Power-up effects, remaining ticks.
    public int BoostTicks;    // Mushroom
    public int StarTicks;     // Star
    public int GhostTicks;    // Boo
    public int SlowTicks;     // hit by Lightning
    public int ReverseTicks;  // hit by Banana

    public bool InGap => GapTicksLeft > 0;
    /// <summary>Star and Boo let the head pass through trails.</summary>
    public bool PassesTrails => StarTicks > 0 || GhostTicks > 0;
    public bool LeavesTrail => !InGap && GhostTicks == 0;

    public Vector2 Direction => new(MathF.Cos(Angle), MathF.Sin(Angle));

    public void ResetEffects() => BoostTicks = StarTicks = GhostTicks = SlowTicks = ReverseTicks = 0;

    public void TickEffects()
    {
        if (BoostTicks > 0) BoostTicks--;
        if (StarTicks > 0) StarTicks--;
        if (GhostTicks > 0) GhostTicks--;
        if (SlowTicks > 0) SlowTicks--;
        if (ReverseTicks > 0) ReverseTicks--;
    }
}
