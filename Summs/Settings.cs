using System.Text.Json;
using System.Text.Json.Serialization;

namespace Summs;

/// <summary>Persisted user settings at %LOCALAPPDATA%\Summs\settings.json.</summary>
sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Summs", "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    public int OverlayX { get; set; } = 100;
    /// <summary>Bottom edge of the overlay; it grows upwards from here.</summary>
    public int OverlayBottom { get; set; } = 400;

    public RowStyle RowStyle { get; set; } = RowStyle.ChampionText;

    public HotkeyConfig Hotkeys { get; set; } = new();

    /// <summary>Shares the timers with the teammates who also run Summs.</summary>
    public bool ShareTimers { get; set; } = true;

    /// <summary>Optional secret mixed into the room id, so only those who know it share timers.</summary>
    public string GroupKey { get; set; } = "";

    /// <summary>Sync server; only set by hand, e.g. to test against a local server.</summary>
    public string? SyncServer { get; set; }

    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            if (settings.Hotkeys is null || settings.Hotkeys.Validate() is not null)
            {
                Log.Write("Atajos guardados no válidos; se usan los predeterminados");
                settings.Hotkeys = new HotkeyConfig();
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException ex)
        {
            Log.Write($"No se pudo guardar la configuración: {ex.Message}");
        }
    }
}
