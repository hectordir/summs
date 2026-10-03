namespace Summs;

/// <summary>Summoner spell cooldowns and colors, keyed by Data Dragon id (e.g. "SummonerFlash").</summary>
static class Spells
{
    // Seconds we assume pass between the spell being used and the hotkey being pressed.
    public const int ReactionDelaySeconds = 10;

    public static readonly Color DefaultColor = Color.FromArgb(205, 210, 214);

    // Used when Data Dragon has never been reachable (values from patch 16.19).
    static readonly Dictionary<string, int> FallbackCooldowns = new()
    {
        ["SummonerFlash"] = 300,
        ["SummonerTeleport"] = 300,
        ["SummonerDot"] = 180,
        ["SummonerHeal"] = 240,
        ["SummonerBarrier"] = 180,
        ["SummonerExhaust"] = 240,
        ["SummonerHaste"] = 240,
        ["SummonerBoost"] = 240,
        ["SummonerMana"] = 240,
        ["SummonerSnowball"] = 80,
    };

    // Data Dragon lists Smite's 15s between casts; what matters is the 90s charge recharge.
    const int SmiteRechargeSeconds = 90;

    // Matched by substring so variants (e.g. upgraded Teleport) share a color.
    static readonly (string IdPart, Color Color)[] Colors =
    {
        ("Flash", Color.FromArgb(245, 197, 66)),     // yellow
        ("Teleport", Color.FromArgb(190, 130, 255)), // purple
        ("Dot", Color.FromArgb(255, 130, 50)),       // Ignite: orange
        ("Heal", Color.FromArgb(255, 140, 180)),     // pink (green is reserved for "ready")
        ("Barrier", Color.FromArgb(255, 220, 160)),  // pale gold
        ("Exhaust", Color.FromArgb(215, 150, 95)),   // brown
        ("Haste", Color.FromArgb(150, 175, 255)),    // Ghost: periwinkle
        ("Boost", Color.FromArgb(90, 220, 240)),     // Cleanse: cyan
        ("Smite", Color.FromArgb(235, 85, 60)),      // red-orange
    };

    // Teleport becomes Unleashed Teleport at 10:00. Data Dragon doesn't list its cooldown, which
    // goes linearly from 330s at level 1 to 240s at level 18 (patch 16.19, League wiki).
    const double UnleashedTeleportGameTime = 10 * 60;
    const int UnleashedTeleportMaxCooldown = 330, UnleashedTeleportMinCooldown = 240;
    public const string UnleashedTeleportName = "Unleashed Teleport";

    /// <summary>
    /// Whether the spell is Unleashed Teleport at this game time: Teleport from 10:00 on, or a
    /// Teleport variant Data Dragon doesn't know (the API may report the upgrade under its own id).
    /// </summary>
    public static bool IsUnleashedTeleport(string spellId, double gameTime) =>
        spellId == "SummonerTeleport"
            ? gameTime >= UnleashedTeleportGameTime
            : spellId.Contains("Teleport") && DataDragon.GetCooldown(spellId) is null;

    /// <summary>Cooldown before haste; Unleashed Teleport's depends on the champion level.</summary>
    public static int? BaseCooldown(string spellId, double gameTime, int level)
    {
        int? cooldown = IsUnleashedTeleport(spellId, gameTime)
            ? UnleashedTeleportCooldown(level)
            : UnscaledCooldown(spellId);
#if PRACTICE
        return cooldown / PracticeData.CooldownDivisor;
#else
        return cooldown;
#endif
    }

    static int UnleashedTeleportCooldown(int level)
    {
        level = Math.Clamp(level, 1, 18);
        double perLevel = (UnleashedTeleportMaxCooldown - UnleashedTeleportMinCooldown) / 17.0;
        return (int)Math.Round(UnleashedTeleportMaxCooldown - perLevel * (level - 1));
    }

    static int? UnscaledCooldown(string spellId)
    {
        if (spellId.Contains("Smite"))
            return SmiteRechargeSeconds;
        return DataDragon.GetCooldown(spellId)
            ?? (FallbackCooldowns.TryGetValue(spellId, out int cd) ? cd : null);
    }

    public static Color ColorOf(string spellId) =>
        Colors.FirstOrDefault(c => spellId.Contains(c.IdPart)).Color is var color && color != default
            ? color
            : DefaultColor;

    /// <summary>Game second at which the spell is back, given haste and the time the hotkey was pressed.</summary>
    public static int ReadyAt(double gameTime, int baseCooldown, int haste)
    {
        double cooldown = baseCooldown * 100.0 / (100 + haste);
        return (int)gameTime + (int)Math.Round(cooldown) - ReactionDelaySeconds;
    }

    /// <summary>Formats game seconds as m:ss.</summary>
    public static string FormatTime(int seconds) => $"{seconds / 60}:{seconds % 60:D2}";
}
