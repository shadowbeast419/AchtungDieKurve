namespace AchtungDieKurve.Net;

/// <summary>A PC connected to the server; it brings 1–3 players (one key set / pad each).</summary>
public sealed record LobbyClient(int Id, string Name, int LocalPlayers, bool IsHost);

/// <summary>Who plays a colour: nobody, the computer, or player <see cref="KeySet"/> of PC <see cref="Owner"/>.</summary>
public readonly record struct SlotAssignment(PlayerKind Kind, int Owner = -1, int KeySet = -1)
{
    public static readonly SlotAssignment Off = new(PlayerKind.Off);
    public static readonly SlotAssignment Ai = new(PlayerKind.Ai);
}

/// <summary>Everything the lobby screen shows; the server sends it whenever something changes.</summary>
public sealed class LobbyState
{
    public bool InGame { get; set; }
    public AiLevel AiLevel { get; set; } = AiLevel.Normal;
    public bool PowerUps { get; set; } = true;
    public List<LobbyClient> Clients { get; set; } = [];
    public SlotAssignment[] Slots { get; set; } = Enumerable.Repeat(SlotAssignment.Off, Settings.SlotCount).ToArray();

    public int ActiveSlots => Slots.Count(s => s.Kind != PlayerKind.Off);

    public LobbyClient? Client(int id) => Clients.FirstOrDefault(c => c.Id == id);

    public void Write(BinaryWriter w)
    {
        w.Write(InGame);
        w.Write((byte)AiLevel);
        w.Write(PowerUps);
        w.Write((byte)Clients.Count);
        foreach (var c in Clients)
        {
            w.Write(c.Id);
            w.Write(c.Name);
            w.Write((byte)c.LocalPlayers);
            w.Write(c.IsHost);
        }
        foreach (var s in Slots)
        {
            w.Write((byte)s.Kind);
            w.Write(s.Owner);
            w.Write((sbyte)s.KeySet);
        }
    }

    public static LobbyState Read(BinaryReader r)
    {
        var state = new LobbyState
        {
            InGame = r.ReadBoolean(),
            AiLevel = ReadEnum<AiLevel>(r.ReadByte()),
            PowerUps = r.ReadBoolean(),
        };
        int count = r.ReadByte();
        for (var i = 0; i < count; i++)
            state.Clients.Add(new LobbyClient(r.ReadInt32(), Protocol.ReadName(r, 32), r.ReadByte(), r.ReadBoolean()));
        for (var i = 0; i < Settings.SlotCount; i++)
            state.Slots[i] = new SlotAssignment(ReadEnum<PlayerKind>(r.ReadByte()), r.ReadInt32(), r.ReadSByte());
        return state;
    }

    public static T ReadEnum<T>(byte value) where T : struct, Enum
    {
        var result = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(result) ? result : throw new InvalidDataException($"bad {typeof(T).Name} {value}");
    }
}
