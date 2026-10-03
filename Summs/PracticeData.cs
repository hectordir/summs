#if PRACTICE
namespace Summs;

/// <summary>
/// Practice build only: the Practice Tool has no enemies or roles, so the real game clock is kept
/// and a fixed enemy lineup is injected so hotkeys and the overlay can be tried out.
/// </summary>
static class PracticeData
{
    // Cooldowns are divided by this so the 60s warning and the "ready" state show up quickly.
    public const int CooldownDivisor = 4;

    public static void Apply(AllGameData data)
    {
        data.ActivePlayer = new ActivePlayer { RiotId = "Tú#PRACTICE" };
        data.AllPlayers = new List<Player>
        {
            Make("Tú", "Ahri", "ORDER", "MIDDLE", "SummonerFlash", "SummonerDot"),
            Make("Darius", "Darius", "CHAOS", "TOP", "SummonerFlash", "SummonerTeleport"),
            Make("Lee Sin", "LeeSin", "CHAOS", "JUNGLE", "SummonerFlash", "SummonerSmite"),
            Make("Syndra", "Syndra", "CHAOS", "MIDDLE", "SummonerFlash", "SummonerDot", inspiration: true),
            Make("Jinx", "Jinx", "CHAOS", "BOTTOM", "SummonerHeal", "SummonerFlash", inspiration: true, 3158),
            Make("Thresh", "Thresh", "CHAOS", "UTILITY", "SummonerFlash", "SummonerExhaust"),
        };
    }

    static Player Make(string name, string key, string team, string position, string spellOne, string spellTwo,
        bool inspiration = false, params int[] items) => new()
    {
        ChampionName = name,
        RawChampionName = "game_character_displayname_" + key,
        RiotId = name + "#PRACTICE",
        Team = team,
        Position = position,
        Items = items.Select(id => new Item { ItemId = id }).ToList(),
        SummonerSpells = new SummonerSpells { One = Spell(spellOne), Two = Spell(spellTwo) },
        // Precision primary; Inspiration or Resolve secondary.
        Runes = new Runes
        {
            Primary = new RuneTree { Id = 8000 },
            Secondary = new RuneTree { Id = inspiration ? Runes.InspirationTreeId : 8400 },
        },
    };

    static SummonerSpell Spell(string id) => new()
    {
        DisplayName = id.Replace("Summoner", ""),
        RawDisplayName = $"GeneratedTip_SummonerSpell_{id}_DisplayName",
    };
}
#endif
