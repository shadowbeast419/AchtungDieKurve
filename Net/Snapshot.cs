using System.Numerics;

namespace AchtungDieKurve.Net;

/// <summary>
/// The state of a running match as sent to clients 60 times a second: phase, heads, scores, effects,
/// boxes, plus everything that happened since the last snapshot (new trail segments, pop-ups, ...).
/// </summary>
public static class Snapshot
{
    private enum EventKind : byte { Segment, Clear, Popup, RoundEnd }

    public static void Write(BinaryWriter w, Match m, IReadOnlyList<MatchEvent> events)
    {
        w.Write(m.Tick);
        w.Write((byte)m.Phase);
        w.Write((short)m.Round);
        w.Write(m.RoundSpeed);
        w.Write(m.PrepareTimer);
        w.Write((sbyte)(m.Winner?.Id ?? -1));
        w.Write(m.RoundMessage);

        w.Write((byte)m.Active.Count);
        foreach (var p in m.Active)
        {
            w.Write((byte)p.Id);
            w.Write(p.Position.X);
            w.Write(p.Position.Y);
            w.Write(p.Angle);
            w.Write(p.Alive);
            w.Write((short)p.Score);
            foreach (var ticks in (int[])[p.BoostTicks, p.StarTicks, p.GhostTicks, p.SlowTicks, p.ReverseTicks])
                w.Write((ushort)Math.Clamp(ticks, 0, ushort.MaxValue));
        }

        w.Write((byte)m.Boxes.Count);
        foreach (var b in m.Boxes)
        {
            w.Write(b.Position.X);
            w.Write(b.Position.Y);
            w.Write(b.SpawnTick);
        }

        w.Write((ushort)events.Count);
        foreach (var e in events)
        {
            switch (e)
            {
                case SegmentEvent s:
                    w.Write((byte)EventKind.Segment);
                    w.Write((byte)s.PlayerId);
                    w.Write(s.A.X); w.Write(s.A.Y); w.Write(s.B.X); w.Write(s.B.Y);
                    break;
                case ClearEvent:
                    w.Write((byte)EventKind.Clear);
                    break;
                case PopupEvent p:
                    w.Write((byte)EventKind.Popup);
                    w.Write((byte)p.Type);
                    w.Write(p.Position.X); w.Write(p.Position.Y);
                    w.Write(p.Tick);
                    break;
                case RoundEndEvent:
                    w.Write((byte)EventKind.RoundEnd);
                    break;
            }
        }
    }

    /// <summary>Copies a snapshot into the client's mirror match; events are appended to <c>m.Events</c>.</summary>
    public static void Apply(BinaryReader r, Match m)
    {
        m.Tick = r.ReadInt32();
        m.Phase = LobbyState.ReadEnum<Phase>(r.ReadByte());
        m.Round = r.ReadInt16();
        m.RoundSpeed = r.ReadSingle();
        m.PrepareTimer = r.ReadSingle();
        var winner = r.ReadSByte();
        m.Winner = winner >= 0 && winner < m.Players.Length ? m.Players[winner] : null;
        m.RoundMessage = Protocol.ReadName(r, 100);

        int count = r.ReadByte();
        var active = new List<Player>(count);
        for (var i = 0; i < count; i++)
        {
            var p = PlayerById(m, r.ReadByte());
            p.Position = new Vector2(r.ReadSingle(), r.ReadSingle());
            p.Angle = r.ReadSingle();
            p.Alive = r.ReadBoolean();
            p.Score = r.ReadInt16();
            p.BoostTicks = r.ReadUInt16();
            p.StarTicks = r.ReadUInt16();
            p.GhostTicks = r.ReadUInt16();
            p.SlowTicks = r.ReadUInt16();
            p.ReverseTicks = r.ReadUInt16();
            active.Add(p);
        }
        if (!active.SequenceEqual(m.Active)) m.Active = active;

        int boxes = r.ReadByte();
        var list = new List<PowerUpBox>(boxes);
        for (var i = 0; i < boxes; i++)
            list.Add(new PowerUpBox(new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadInt32()));
        m.SetBoxes(list);

        int events = r.ReadUInt16();
        for (var i = 0; i < events; i++)
        {
            switch (LobbyState.ReadEnum<EventKind>(r.ReadByte()))
            {
                case EventKind.Segment:
                    var id = PlayerById(m, r.ReadByte()).Id;
                    m.Events.Add(new SegmentEvent(new Vector2(r.ReadSingle(), r.ReadSingle()), new Vector2(r.ReadSingle(), r.ReadSingle()), id));
                    break;
                case EventKind.Clear:
                    m.Events.Add(new ClearEvent());
                    break;
                case EventKind.Popup:
                    var type = LobbyState.ReadEnum<PowerUpType>(r.ReadByte());
                    m.Events.Add(new PopupEvent(type, new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadInt32()));
                    break;
                case EventKind.RoundEnd:
                    m.Events.Add(new RoundEndEvent());
                    break;
            }
        }
    }

    private static Player PlayerById(Match m, byte id) =>
        id < m.Players.Length ? m.Players[id] : throw new InvalidDataException("bad player id");
}
