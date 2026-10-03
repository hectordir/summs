using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Summs;

/// <summary>
/// Client for Riot's local Live Client Data API, only available while a game is running.
/// </summary>
static class LiveClient
{
    const string BaseUrl = "https://127.0.0.1:2999/liveclientdata/";

    static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The game serves a self-signed certificate on localhost.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>Returns the current game time in seconds, or null if no game is running.</summary>
    public static async Task<double?> GetGameTimeAsync()
    {
        var stats = await GetAsync<GameStats>("gamestats");
        return stats?.GameTime;
    }

    /// <summary>Returns the full game snapshot, or null if no game is running.</summary>
    public static Task<AllGameData?> GetAllGameDataAsync() => GetAsync<AllGameData>("allgamedata");

    static async Task<T?> GetAsync<T>(string endpoint) where T : class
    {
        try
        {
            return await Http.GetFromJsonAsync<T>(BaseUrl + endpoint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    sealed class GameStats
    {
        [JsonPropertyName("gameTime")]
        public double GameTime { get; set; }
    }
}

sealed class AllGameData
{
    [JsonPropertyName("activePlayer")]
    public ActivePlayer? ActivePlayer { get; set; }

    [JsonPropertyName("allPlayers")]
    public List<Player> AllPlayers { get; set; } = new();

    [JsonPropertyName("gameData")]
    public GameInfo? GameData { get; set; }

    /// <summary>The team the local player is on ("ORDER" or "CHAOS"), or null if not found.</summary>
    public string? OwnTeam()
    {
        if (ActivePlayer is null)
            return null;

        var me = AllPlayers.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(ActivePlayer.RiotId) && p.RiotId == ActivePlayer.RiotId)
            || (!string.IsNullOrEmpty(ActivePlayer.SummonerName) && p.SummonerName == ActivePlayer.SummonerName));
        return me?.Team;
    }
}

sealed class ActivePlayer
{
    [JsonPropertyName("riotId")]
    public string? RiotId { get; set; }

    [JsonPropertyName("summonerName")]
    public string? SummonerName { get; set; }
}

sealed class GameInfo
{
    [JsonPropertyName("gameTime")]
    public double GameTime { get; set; }
}

sealed class Player
{
    [JsonPropertyName("championName")]
    public string ChampionName { get; set; } = "";

    /// <summary>e.g. "game_character_displayname_MonkeyKing"; the suffix is the Data Dragon key.</summary>
    [JsonPropertyName("rawChampionName")]
    public string RawChampionName { get; set; } = "";

    [JsonPropertyName("riotId")]
    public string? RiotId { get; set; }

    [JsonPropertyName("summonerName")]
    public string? SummonerName { get; set; }

    /// <summary>"TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY", or empty when the game doesn't assign roles.</summary>
    [JsonPropertyName("position")]
    public string Position { get; set; } = "";

    [JsonPropertyName("team")]
    public string Team { get; set; } = "";

    [JsonPropertyName("level")]
    public int Level { get; set; } = 1;

    [JsonPropertyName("items")]
    public List<Item> Items { get; set; } = new();

    [JsonPropertyName("summonerSpells")]
    public SummonerSpells? SummonerSpells { get; set; }

    /// <summary>For other players the API only gives the keystone and the two trees, not every rune.</summary>
    [JsonPropertyName("runes")]
    public Runes? Runes { get; set; }

    public string ChampionKey => RawChampionName.Replace("game_character_displayname_", "");
}

sealed class Runes
{
    public const int InspirationTreeId = 8300;

    [JsonPropertyName("primaryRuneTree")]
    public RuneTree? Primary { get; set; }

    [JsonPropertyName("secondaryRuneTree")]
    public RuneTree? Secondary { get; set; }

    /// <summary>
    /// Whether they may have Cosmic Insight: it's in the Inspiration tree, and which runes of a
    /// tree were picked isn't exposed for other players.
    /// </summary>
    public bool HasInspiration => Primary?.Id == InspirationTreeId || Secondary?.Id == InspirationTreeId;
}

sealed class RuneTree
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

sealed class Item
{
    [JsonPropertyName("itemID")]
    public int ItemId { get; set; }
}

sealed class SummonerSpells
{
    [JsonPropertyName("summonerSpellOne")]
    public SummonerSpell? One { get; set; }

    [JsonPropertyName("summonerSpellTwo")]
    public SummonerSpell? Two { get; set; }

    /// <summary>
    /// Spell for a hotkey slot regardless of D/F: slot 1 is Flash and slot 2 the other spell.
    /// Without Flash, slot 1 is D and slot 2 is F.
    /// </summary>
    public SummonerSpell? ForSlot(int slot)
    {
        bool flashOnF = Two?.Id == "SummonerFlash" && One?.Id != "SummonerFlash";
        return (slot == 1) != flashOnF ? One : Two;
    }
}

sealed class SummonerSpell
{
    /// <summary>Localized name, in the game's language.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>e.g. "GeneratedTip_SummonerSpell_SummonerFlash_DisplayName".</summary>
    [JsonPropertyName("rawDisplayName")]
    public string RawDisplayName { get; set; } = "";

    /// <summary>Data Dragon id, e.g. "SummonerFlash".</summary>
    public string Id => RawDisplayName
        .Replace("GeneratedTip_SummonerSpell_", "")
        .Replace("_DisplayName", "");
}
