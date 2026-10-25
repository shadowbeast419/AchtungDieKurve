using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace AchtungDieKurve;

public enum PowerUpType { Mushroom, Star, Boo, Lightning, Banana, Bomb }

public enum PowerUpTarget { Self, Others, Board }

/// <summary>A Mario-Kart-style "?" box; the item inside is rolled when it is picked up.</summary>
public sealed record PowerUpBox(Vector2 Position, int SpawnTick);

public static class PowerUps
{
    public const float BoxHalfSize = 13f;

    private static readonly (PowerUpType Type, int Weight)[] Weights =
    [
        (PowerUpType.Mushroom, 3), (PowerUpType.Star, 2), (PowerUpType.Boo, 2),
        (PowerUpType.Lightning, 2), (PowerUpType.Banana, 2), (PowerUpType.Bomb, 1),
    ];

    public static PowerUpType Roll(Random rng)
    {
        var n = rng.Next(Weights.Sum(w => w.Weight));
        foreach (var (type, weight) in Weights)
            if ((n -= weight) < 0) return type;
        return PowerUpType.Mushroom;
    }

    public static PowerUpTarget Target(PowerUpType type) => type switch
    {
        PowerUpType.Lightning or PowerUpType.Banana => PowerUpTarget.Others,
        PowerUpType.Bomb => PowerUpTarget.Board,
        _ => PowerUpTarget.Self,
    };

    public static string Text(PowerUpType type) => type switch
    {
        PowerUpType.Mushroom => "Mushroom! Speed boost",
        PowerUpType.Star => "Star! Invincible",
        PowerUpType.Boo => "Boo! Invisible",
        PowerUpType.Lightning => "Lightning! Others slowed",
        PowerUpType.Banana => "Banana! Others reversed",
        PowerUpType.Bomb => "Bob-omb! Board cleared",
        _ => "",
    };

    public static Color TargetColor(PowerUpTarget target) => target switch
    {
        PowerUpTarget.Self => new Color(90, 230, 90, 255),
        PowerUpTarget.Others => new Color(255, 90, 90, 255),
        _ => new Color(255, 210, 60, 255),
    };

    // ───────────────────────────── Drawing ─────────────────────────────

    public static void DrawBox(PowerUpBox box, int tick, int lifetimeTicks)
    {
        var age = tick - box.SpawnTick;
        // Blink during the last 3 seconds.
        if (lifetimeTicks - age < 360 && age / 15 % 2 == 0) return;

        var pulse = 1f + 0.08f * MathF.Sin(age * 0.1f);
        var h = BoxHalfSize * pulse;
        var rect = new Rectangle(box.Position.X - h, box.Position.Y - h, 2 * h, 2 * h);
        DrawRectangleRounded(rect, 0.35f, 6, new Color(25, 25, 40, 235));
        DrawRectangleRoundedLinesEx(rect, 0.35f, 6, 2.5f, ColorFromHSV(age * 3 % 360, 0.8f, 1f));

        var size = (int)(20 * pulse);
        DrawText("?", (int)(box.Position.X - MeasureText("?", size) / 2f), (int)(box.Position.Y - size / 2f), size, Color.White);
    }

    /// <summary>Draws the item icon centred on c; s ≈ half the icon size.</summary>
    public static void DrawIcon(PowerUpType type, Vector2 c, float s, int tick)
    {
        switch (type)
        {
            case PowerUpType.Mushroom:
                DrawCircleSector(c with { Y = c.Y + 0.1f * s }, 0.95f * s, 180, 360, 24, new Color(220, 40, 40, 255));
                DrawCircleV(c + new Vector2(-0.5f, -0.3f) * s, 0.2f * s, Color.White);
                DrawCircleV(c + new Vector2(0.45f, -0.35f) * s, 0.2f * s, Color.White);
                DrawCircleV(c + new Vector2(0f, -0.7f) * s, 0.16f * s, Color.White);
                DrawRectangleRounded(new Rectangle(c.X - 0.38f * s, c.Y + 0.1f * s, 0.76f * s, 0.8f * s), 0.5f, 4, new Color(245, 225, 190, 255));
                DrawRectangleV(c + new Vector2(-0.2f, 0.25f) * s, new Vector2(0.1f, 0.25f) * s, Color.Black);
                DrawRectangleV(c + new Vector2(0.1f, 0.25f) * s, new Vector2(0.1f, 0.25f) * s, Color.Black);
                break;

            case PowerUpType.Star:
                DrawStar(c, s, new Color(255, 215, 40, 255));
                DrawRectangleV(c + new Vector2(-0.22f, -0.15f) * s, new Vector2(0.1f, 0.3f) * s, Color.Black);
                DrawRectangleV(c + new Vector2(0.12f, -0.15f) * s, new Vector2(0.1f, 0.3f) * s, Color.Black);
                break;

            case PowerUpType.Boo:
                DrawCircleV(c with { Y = c.Y - 0.1f * s }, 0.8f * s, Color.RayWhite);
                DrawRectangleV(c + new Vector2(-0.8f, -0.1f) * s, new Vector2(1.6f, 0.65f) * s, Color.RayWhite);
                for (var i = 0; i < 3; i++)
                    DrawCircleV(c + new Vector2(-0.53f + i * 0.53f, 0.55f) * s, 0.27f * s, Color.RayWhite);
                DrawRectangleV(c + new Vector2(-0.38f, -0.4f) * s, new Vector2(0.14f, 0.3f) * s, Color.Black);
                DrawRectangleV(c + new Vector2(0.24f, -0.4f) * s, new Vector2(0.14f, 0.3f) * s, Color.Black);
                DrawCircleV(c + new Vector2(0f, 0.15f) * s, 0.18f * s, new Color(200, 60, 80, 255));
                break;

            case PowerUpType.Lightning:
            {
                var col = new Color(255, 220, 40, 255);
                Vector2[] pts = [new(0.35f, -0.95f), new(-0.3f, 0.05f), new(0.3f, -0.05f), new(-0.3f, 0.95f)];
                for (var i = 0; i < pts.Length - 1; i++)
                {
                    DrawLineEx(c + pts[i] * s, c + pts[i + 1] * s, 0.32f * s, col);
                    DrawCircleV(c + pts[i + 1] * s, 0.16f * s, col);
                }
                break;
            }

            case PowerUpType.Banana:
                DrawRing(c with { Y = c.Y - 0.75f * s }, 0.95f * s, 1.45f * s, 35, 145, 16, new Color(250, 220, 60, 255));
                DrawCircleV(c + new Vector2(0.98f, 0.0f) * s, 0.16f * s, new Color(110, 80, 30, 255));
                DrawCircleV(c + new Vector2(-0.98f, 0.0f) * s, 0.16f * s, new Color(110, 80, 30, 255));
                break;

            case PowerUpType.Bomb:
                DrawLineEx(c + new Vector2(0.3f, -0.5f) * s, c + new Vector2(0.6f, -0.95f) * s, 0.15f * s, new Color(150, 110, 60, 255));
                DrawCircleV(c with { Y = c.Y + 0.15f * s }, 0.75f * s, new Color(45, 45, 55, 255));
                DrawCircleV(c + new Vector2(-0.25f, -0.15f) * s, 0.18f * s, new Color(120, 120, 140, 255));
                DrawCircleV(c + new Vector2(0.65f, -1f) * s, (tick / 6 % 2 == 0 ? 0.25f : 0.17f) * s, Color.Orange);
                break;
        }
    }

    private static void DrawStar(Vector2 c, float s, Color color)
    {
        var pts = new Vector2[10];
        for (var i = 0; i < 10; i++)
        {
            var a = -MathF.PI / 2 + i * MathF.PI / 5;
            var r = i % 2 == 0 ? s : 0.45f * s;
            pts[i] = c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        }
        for (var i = 0; i < 10; i++)
        {
            // Draw both windings so the triangle shows regardless of orientation.
            DrawTriangle(c, pts[i], pts[(i + 1) % 10], color);
            DrawTriangle(c, pts[(i + 1) % 10], pts[i], color);
        }
    }
}

/// <summary>Spawns, expires and hands out "?" boxes.</summary>
public sealed class PowerUpSpawner
{
    public const int LifetimeTicks = 15 * 120;
    private const int MaxBoxes = 3;
    private const float PickupRadius = 15f;
    private const float WallMargin = 40f;

    private readonly List<PowerUpBox> _boxes = [];
    private int _nextSpawnTick;

    public IReadOnlyList<PowerUpBox> Boxes => _boxes;

    public void Reset(int tick, Random rng)
    {
        _boxes.Clear();
        _nextSpawnTick = tick + NextDelay(rng);
    }

    public void Update(int tick, Field field, IEnumerable<Player> alive, Random rng)
    {
        _boxes.RemoveAll(b => tick - b.SpawnTick > LifetimeTicks);
        if (tick < _nextSpawnTick) return;
        _nextSpawnTick = tick + NextDelay(rng);
        if (_boxes.Count >= MaxBoxes) return;

        var heads = alive.Select(p => p.Position).ToList();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var pos = new Vector2(
                WallMargin + rng.NextSingle() * (field.Width - 2 * WallMargin),
                WallMargin + rng.NextSingle() * (field.Height - 2 * WallMargin));
            if (!field.IsFreeArea(pos, PowerUps.BoxHalfSize + 6)) continue;
            if (heads.Any(h => Vector2.Distance(h, pos) < 70)) continue;
            if (_boxes.Any(b => Vector2.Distance(b.Position, pos) < 80)) continue;
            _boxes.Add(new PowerUpBox(pos, tick));
            return;
        }
    }

    /// <summary>Network clients: replace the boxes with the server's.</summary>
    public void SetBoxes(IEnumerable<PowerUpBox> boxes)
    {
        _boxes.Clear();
        _boxes.AddRange(boxes);
    }

    public bool TryPickup(Player p)
    {
        var idx = _boxes.FindIndex(b => Vector2.Distance(b.Position, p.Position) < PickupRadius);
        if (idx < 0) return false;
        _boxes.RemoveAt(idx);
        return true;
    }

    private static int NextDelay(Random rng) => (int)(120 * (3f + rng.NextSingle() * 3f));
}
