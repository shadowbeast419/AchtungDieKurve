using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchtungDieKurve;

/// <summary>Menu choices, persisted to ~/.config/achtung-die-kurve/settings.json.</summary>
public sealed class Settings
{
    public const int SlotCount = 6;
    /// <summary>Slots with keyboard keys; the others can only be AI.</summary>
    public const int HumanSlots = 3;

    public PlayerKind[] Slots { get; set; } =
        [PlayerKind.Human, PlayerKind.Human, PlayerKind.Human, PlayerKind.Off, PlayerKind.Off, PlayerKind.Off];
    public AiLevel AiLevel { get; set; } = AiLevel.Normal;
    public bool PowerUps { get; set; } = true;

    // Network play
    public const int DefaultPort = 7777;
    public const int MaxLocalPlayers = 3;
    public string PlayerName { get; set; } = Environment.MachineName;
    public string LastServer { get; set; } = "localhost";
    public int Port { get; set; } = DefaultPort;
    /// <summary>How many people play on this PC in a network game.</summary>
    public int LocalPlayers { get; set; } = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "achtung-die-kurve", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) is { } s)
            {
                if (s.Slots.Length != SlotCount) s.Slots = new Settings().Slots;
                for (var i = HumanSlots; i < SlotCount; i++)
                    if (s.Slots[i] == PlayerKind.Human) s.Slots[i] = PlayerKind.Ai;
                s.LocalPlayers = Math.Clamp(s.LocalPlayers, 1, MaxLocalPlayers);
                if (s.Port is < 1 or > 65535) s.Port = DefaultPort;
                s.PlayerName = string.IsNullOrWhiteSpace(s.PlayerName) ? Environment.MachineName : s.PlayerName.Trim();
                s.LastServer ??= "localhost";
                return s;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not read settings, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not save settings: {e.Message}");
        }
    }
}
