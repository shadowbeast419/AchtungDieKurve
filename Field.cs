using System.Numerics;

namespace AchtungDieKurve;

/// <summary>CPU-side collision grid: which player painted each logical pixel, and when.</summary>
public sealed class Field(int width, int height)
{
    /// <summary>A player's own trail younger than this is ignored, so the head doesn't hit its neck.</summary>
    public const int SelfGraceTicks = 30;

    private readonly byte[] _owner = new byte[width * height];   // 0 = empty, else player id + 1
    private readonly int[] _tick = new int[width * height];

    public int Width { get; } = width;
    public int Height { get; } = height;

    public void Clear()
    {
        Array.Clear(_owner);
        Array.Clear(_tick);
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool IsOccupied(int x, int y) => InBounds(x, y) && _owner[y * Width + x] != 0;

    /// <summary>True if the cell holds a trail that kills <paramref name="playerId"/>. Ignores walls.</summary>
    public bool IsTrail(int x, int y, int playerId, int now)
    {
        if (!InBounds(x, y)) return false;
        var i = y * Width + x;
        var owner = _owner[i];
        if (owner == 0) return false;
        return owner != playerId + 1 || now - _tick[i] > SelfGraceTicks;
    }

    /// <summary>Paints a thick line from a to b (sub-stepped so fast heads leave no holes).</summary>
    public void StampSegment(Vector2 a, Vector2 b, float radius, int playerId, int now)
    {
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b)));
        for (var s = 1; s <= steps; s++) StampCircle(Vector2.Lerp(a, b, s / (float)steps), radius, playerId, now);
    }

    private void StampCircle(Vector2 c, float radius, int playerId, int now)
    {
        var r = (int)MathF.Ceiling(radius);
        int cx = (int)c.X, cy = (int)c.Y;
        var r2 = radius * radius;
        for (var y = cy - r; y <= cy + r; y++)
        for (var x = cx - r; x <= cx + r; x++)
        {
            if (!InBounds(x, y)) continue;
            float dx = x + 0.5f - c.X, dy = y + 0.5f - c.Y;
            if (dx * dx + dy * dy > r2) continue;
            var i = y * Width + x;
            _owner[i] = (byte)(playerId + 1);
            _tick[i] = now;
        }
    }

    /// <summary>True if no trail lies within <paramref name="radius"/> of the point (sampled).</summary>
    public bool IsFreeArea(Vector2 c, float radius)
    {
        for (var dy = -radius; dy <= radius; dy += 2)
        for (var dx = -radius; dx <= radius; dx += 2)
        {
            if (dx * dx + dy * dy > radius * radius) continue;
            int x = (int)(c.X + dx), y = (int)(c.Y + dy);
            if (!InBounds(x, y) || _owner[y * Width + x] != 0) return false;
        }
        return true;
    }
}
