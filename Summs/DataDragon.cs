using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Summs;

/// <summary>
/// Patch data from Riot's public Data Dragon CDN: summoner spell cooldowns, items and the
/// Cosmic Insight rune that grant summoner spell haste, and champion/spell icons. Everything is cached under
/// %LOCALAPPDATA%\Summs\ddragon so it keeps working offline with the last downloaded patch.
/// </summary>
static partial class DataDragon
{
    const string BaseUrl = "https://ddragon.leagueoflegends.com/";

    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Summs", "ddragon");
    static readonly string DataFile = Path.Combine(CacheDir, "data.json");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static CachedData _data = new();

    [GeneratedRegex(@"Gain (\d+) Summoner Spell Haste")]
    private static partial Regex HasteRegex();

    [GeneratedRegex(@"(\d+) Summoner Spell Haste")]
    private static partial Regex RuneHasteRegex();

    const int CosmicInsightId = 8347;
    const int CosmicInsightFallbackHaste = 18;

    sealed class CachedData
    {
        public string? Version { get; set; }
        public Dictionary<string, int> Cooldowns { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new();
        public Dictionary<int, int> ItemHaste { get; set; } = new();
        public int? CosmicInsightHaste { get; set; }
    }

    public static string? Version => _data.Version;

    /// <summary>Loads the cache, then refreshes it if a newer patch is available.</summary>
    public static async Task InitAsync()
    {
        try
        {
            if (File.Exists(DataFile))
                _data = JsonSerializer.Deserialize<CachedData>(await File.ReadAllTextAsync(DataFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Data Dragon: caché ilegible: {ex.Message}");
        }

        try
        {
            var versions = await Http.GetFromJsonAsync<List<string>>(BaseUrl + "api/versions.json");
            string? latest = versions?.FirstOrDefault();
            // Caches from before the Cosmic Insight value was stored are refreshed once.
            if (latest is null || (latest == _data.Version && _data.CosmicInsightHaste is not null))
                return;

            var data = new CachedData { Version = latest };

            using (var summoners = JsonDocument.Parse(await Http.GetStringAsync($"{BaseUrl}cdn/{latest}/data/en_US/summoner.json")))
            {
                foreach (var spell in summoners.RootElement.GetProperty("data").EnumerateObject())
                {
                    data.Cooldowns[spell.Name] = (int)spell.Value.GetProperty("cooldown")[0].GetDouble();
                    data.Names[spell.Name] = spell.Value.GetProperty("name").GetString() ?? spell.Name;
                }
            }

            using (var items = JsonDocument.Parse(await Http.GetStringAsync($"{BaseUrl}cdn/{latest}/data/en_US/item.json")))
            {
                foreach (var item in items.RootElement.GetProperty("data").EnumerateObject())
                {
                    string description = item.Value.GetProperty("description").GetString() ?? "";
                    var match = HasteRegex().Match(Regex.Replace(description, "<[^>]+>", " "));
                    if (match.Success && int.TryParse(item.Name, out int id))
                        data.ItemHaste[id] = int.Parse(match.Groups[1].Value);
                }
            }

            using (var runes = JsonDocument.Parse(await Http.GetStringAsync($"{BaseUrl}cdn/{latest}/data/en_US/runesReforged.json")))
            {
                var cosmicInsight = runes.RootElement.EnumerateArray()
                    .SelectMany(tree => tree.GetProperty("slots").EnumerateArray())
                    .SelectMany(slot => slot.GetProperty("runes").EnumerateArray())
                    .FirstOrDefault(rune => rune.GetProperty("id").GetInt32() == CosmicInsightId);
                if (cosmicInsight.ValueKind == JsonValueKind.Object)
                {
                    string description = cosmicInsight.GetProperty("shortDesc").GetString() ?? "";
                    var match = RuneHasteRegex().Match(Regex.Replace(description, "<[^>]+>", ""));
                    if (match.Success)
                        data.CosmicInsightHaste = int.Parse(match.Groups[1].Value);
                }
            }

            _data = data;
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(DataFile, JsonSerializer.Serialize(data));
            Log.Write($"Data Dragon: actualizado al parche {latest}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or IOException)
        {
            Log.Write($"Data Dragon: no se pudo actualizar ({ex.Message}); usando parche {_data.Version ?? "ninguno"}");
        }
    }

    /// <summary>Base cooldown in seconds for a spell id like "SummonerFlash", if known.</summary>
    public static int? GetCooldown(string spellId) =>
        _data.Cooldowns.TryGetValue(spellId, out int cd) ? cd : null;

    /// <summary>English name for a spell id like "SummonerFlash", if known.</summary>
    public static string? GetName(string spellId) =>
        _data.Names.TryGetValue(spellId, out string? name) ? name : null;

    /// <summary>Summoner spell haste granted by the given items (e.g. Ionian Boots of Lucidity).</summary>
    public static int GetSummonerHaste(IEnumerable<int> itemIds)
    {
        // Fallback for when Data Dragon has never been reachable.
        var table = _data.ItemHaste.Count > 0 ? _data.ItemHaste : new Dictionary<int, int> { [3158] = 10, [3171] = 20 };
        return itemIds.Sum(id => table.TryGetValue(id, out int haste) ? haste : 0);
    }

    /// <summary>Summoner spell haste of the Cosmic Insight rune (Inspiration tree).</summary>
    public static int CosmicInsightHaste => _data.CosmicInsightHaste ?? CosmicInsightFallbackHaste;

    /// <summary>
    /// Downloads (or reads from disk) an icon. Kind is "champion" or "spell"; key is the
    /// Data Dragon key, e.g. "MonkeyKing" or "SummonerFlash". Returns null if unavailable.
    /// </summary>
    public static async Task<Image?> LoadIconAsync(string kind, string key)
    {
        string file = Path.Combine(CacheDir, kind, key + ".png");
        try
        {
            if (!File.Exists(file))
            {
                if (_data.Version is null)
                    return null;

                byte[] bytes = await Http.GetByteArrayAsync($"{BaseUrl}cdn/{_data.Version}/img/{kind}/{key}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
            }

            // Copy into memory so the file isn't kept locked.
            using var stream = new MemoryStream(await File.ReadAllBytesAsync(file));
            return new Bitmap(Image.FromStream(stream));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ArgumentException)
        {
            Log.Write($"Icono {kind}/{key} no disponible: {ex.Message}");
            return null;
        }
    }
}
