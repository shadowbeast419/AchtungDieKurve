using System.Numerics;
using Raylib_cs;

namespace AchtungDieKurve;

public enum PlayerKind { Off, Human, Ai }

public sealed class Player(int id, string name, Color color)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public Color Color { get; } = color;

    /// <summary>The six colours of the original game.</summary>
    public static Player[] CreateAll() =>
    [
        new(0, "Fred",     new Color(235, 50, 50, 255)),
        new(1, "Greenlee", new Color(60, 200, 60, 255)),
        new(2, "Bluebell", new Color(60, 140, 255, 255)),
        new(3, "Pinkney",  new Color(255, 110, 200, 255)),
        new(4, "Willem",   new Color(255, 150, 40, 255)),
        new(5, "Greydon",  new Color(175, 175, 175, 255)),
    ];

    public PlayerKind Kind;
    /// <summary>Position among the human players (0-based); the N-th controller goes to the N-th human.</summary>
    public int HumanIndex = -1;
    /// <summary>Key set (see <see cref="Input.KeySets"/>) used by this human on its PC, or -1.</summary>
    public int KeySet = -1;
    /// <summary>Network games: id of the PC that controls this human (-1 = local / AI).</summary>
    public int OwnerClient = -1;
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
