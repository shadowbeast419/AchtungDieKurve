using System.Numerics;

namespace AchtungDieKurve;

public enum AiLevel { Easy, Normal, Hard }

/// <summary>What an AI needs to know about the world for one decision.</summary>
public readonly record struct AiWorld(
    Field Field, int Tick, float StepPerTick, float TurnPerTick, float SpeedRatio, IReadOnlyList<PowerUpBox> Boxes);

/// <summary>
/// Steering AI: projects arcs for "left / straight / right" through the collision grid and picks
/// the one with the most free room. Easy looks less far, reacts slower and sometimes blunders.
/// </summary>
public sealed class AiBrain(AiLevel level, Random rng)
{
    private const float ProbeStep = 3f;
    private const float WallMargin = 5f;
    private const float LateralOffset = 3f;
    private static readonly float[] Actions = [-1f, 0f, 1f];

    private int _cooldown;
    private float _action;

    private (float Lookahead, int Interval, double MistakeChance, bool TwoStage) Profile => level switch
    {
        AiLevel.Easy => (45f, 16, 0.07, false),
        AiLevel.Hard => (240f, 2, 0.0, true),
        _ => (140f, 5, 0.0, false),
    };

    public float Decide(Player p, AiWorld w)
    {
        if (--_cooldown > 0) return _action;

        var (lookahead, interval, mistakeChance, twoStage) = Profile;
        _cooldown = interval;
        lookahead *= MathF.Max(1f, w.SpeedRatio);

        if (rng.NextDouble() < mistakeChance)
        {
            _action = Actions[rng.Next(Actions.Length)];
            return _action;
        }

        var preferred = PreferredTowardsBox(p, w);
        var bestScore = float.NegativeInfinity;
        var bestAction = 0f;
        foreach (var a in Actions)
        {
            float score;
            if (twoStage)
            {
                var half = lookahead / 2;
                var (dist, pos, angle) = Probe(p, w, p.Position, p.Angle, a, half, 0);
                score = dist < half
                    ? dist
                    : dist + Actions.Max(a2 => Probe(p, w, pos, angle, a2, half, dist).Dist);
            }
            else
            {
                score = Probe(p, w, p.Position, p.Angle, a, lookahead, 0).Dist;
            }

            var clear = score >= lookahead - 0.01f;
            if (clear && preferred == a) score += 5;   // go for a "?" box when it is safe
            if (a == _action) score += 2;              // avoid jitter
            if (a == 0) score += 1;                    // prefer straight

            if (score > bestScore) { bestScore = score; bestAction = a; }
        }

        _action = bestAction;
        return _action;
    }

    /// <summary>Follows an arc with constant steering; returns how far it got before hitting something.</summary>
    private static (float Dist, Vector2 Pos, float Angle) Probe(
        Player p, AiWorld w, Vector2 pos, float angle, float action, float maxDist, float alreadyTravelled)
    {
        var turnPerStep = action * w.TurnPerTick * (ProbeStep / w.StepPerTick);
        var field = w.Field;

        for (var d = ProbeStep; d <= maxDist; d += ProbeStep)
        {
            angle += turnPerStep;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            pos += dir * ProbeStep;

            // Effects that run out on the way no longer protect us.
            var ticksAhead = (alreadyTravelled + d) / w.StepPerTick;
            var star = p.StarTicks > ticksAhead;
            var passesTrails = star || p.GhostTicks > ticksAhead;

            if (pos.X < WallMargin || pos.Y < WallMargin || pos.X > field.Width - WallMargin || pos.Y > field.Height - WallMargin)
            {
                if (!star) return (d - ProbeStep, pos, angle);
                pos = new Vector2((pos.X + field.Width) % field.Width, (pos.Y + field.Height) % field.Height);
            }

            if (passesTrails) continue;
            var side = new Vector2(-dir.Y, dir.X) * LateralOffset;
            if (Hit(pos) || Hit(pos + side) || Hit(pos - side)) return (d - ProbeStep, pos, angle);
        }
        return (maxDist, pos, angle);

        bool Hit(Vector2 v) => w.Field.IsTrail((int)v.X, (int)v.Y, p.Id, w.Tick);
    }

    /// <summary>Steering towards the nearest "?" box within reach, or NaN if there is none.</summary>
    private static float PreferredTowardsBox(Player p, AiWorld w)
    {
        PowerUpBox? nearest = null;
        var best = 250f;
        foreach (var b in w.Boxes)
        {
            var d = Vector2.Distance(b.Position, p.Position);
            if (d < best) { best = d; nearest = b; }
        }
        if (nearest is null) return float.NaN;

        var to = nearest.Position - p.Position;
        var diff = MathF.Atan2(to.Y, to.X) - p.Angle;
        diff = MathF.IEEERemainder(diff, MathF.Tau);   // -π..π
        return MathF.Abs(diff) < 0.15f ? 0f : MathF.Sign(diff);
    }
}
