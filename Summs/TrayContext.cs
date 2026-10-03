using System.Text.Json;
using System.Text.RegularExpressions;

namespace Summs;

sealed class TrayContext : ApplicationContext
{
#if PRACTICE
    const string AppName = "Summs (práctica)";
#else
    const string AppName = "Summs";
#endif

    // Role number (see HotkeyConfig.RoleNames) -> overlay label and the Live Client API "position" of that role.
    static readonly Dictionary<int, (string Label, string Position)> Roles = new()
    {
        [1] = ("TOP", "TOP"),
        [2] = ("JG", "JUNGLE"),
        [3] = ("MID", "MIDDLE"),
        [4] = ("ADC", "BOTTOM"),
        [5] = ("SUP", "UTILITY"),
    };

    readonly NotifyIcon _tray;
    readonly Icon? _icon = LoadIcon();
    readonly KeyboardHook _keyboard;
    readonly OverlayForm _overlay;
    readonly Settings _settings = Settings.Load();
    readonly System.Windows.Forms.Timer _ticker = new() { Interval = 1000 };
    readonly ToolStripMenuItem _pauseItem;
    readonly ToolStripMenuItem _placeholderItem;
    readonly ToolStripMenuItem _championTextItem;
    readonly ToolStripMenuItem _roleIconsItem;
    readonly ToolStripMenuItem _shareItem;
    readonly SyncClient _sync;
    // Enemies (by champion key) the user confirmed have, or don't have, Cosmic Insight this game.
    readonly Dictionary<string, bool> _cosmicInsight = new();
    double _lastGameTime;
    HotkeyForm? _hotkeyForm;
    GroupKeyForm? _groupKeyForm;
    double? _gameTime; // as of the last tick; null with no game
    bool _roomResolved; // the room for this game was computed (or sharing is off)
    int _peers;
    bool _paused;
    bool _ticking;

    public TrayContext()
    {
        _pauseItem = new ToolStripMenuItem("Pausar", null, (_, _) => TogglePause());
        _placeholderItem = new ToolStripMenuItem("Mostrar para colocar", null, (_, _) => TogglePlaceholder());
        _championTextItem = new ToolStripMenuItem("Campeón + texto", null, (_, _) => SetRowStyle(RowStyle.ChampionText));
        _roleIconsItem = new ToolStripMenuItem("Rol + iconos", null, (_, _) => SetRowStyle(RowStyle.RoleIcons));

        var styleMenu = new ToolStripMenuItem("Estilo de fila");
        styleMenu.DropDownItems.Add(_championTextItem);
        styleMenu.DropDownItems.Add(_roleIconsItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(styleMenu);
        menu.Items.Add(_placeholderItem);
        menu.Items.Add("Atajos...", null, (_, _) => EditHotkeys());
        _shareItem = new ToolStripMenuItem("Compartir timers", null, (_, _) => ToggleShare()) { Checked = _settings.ShareTimers };
        menu.Items.Add(_shareItem);
        menu.Items.Add("Clave de grupo...", null, (_, _) => EditGroupKey());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _icon ?? SystemIcons.Application,
            Text = AppName + " - activo",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _overlay = new OverlayForm(_settings.OverlayX, _settings.OverlayBottom, _settings.RowStyle);
        _overlay.Moved += (_, _) =>
        {
            (_settings.OverlayX, _settings.OverlayBottom) = _overlay.AnchorPoint;
            _settings.Save();
        };
        _overlay.CosmicInsightAnswered += (_, answer) =>
        {
            _cosmicInsight[answer.ChampionKey] = answer.HasIt;
            _overlay.SetCosmicInsight(answer.ChampionKey, answer.HasIt);
            Log.Write($"{answer.ChampionKey}: Perspicacia cósmica {(answer.HasIt ? "confirmada" : "descartada")}");
            ShareInsight(answer.ChampionKey, answer.HasIt, _gameTime ?? 0);
        };
        _overlay.TimerAdjusted += (_, timer) => ShareTimer(timer, _gameTime ?? timer.PressedAt);
        _overlay.TimerDeleted += (_, key) => ShareRemoval(key.Role, key.Slot);
        UpdateStyleChecks();

        _sync = new SyncClient(_settings.SyncServer ?? SyncClient.DefaultServer);
        _sync.ItemReceived += OnSyncItem;
        _sync.PeersChanged += OnPeersChanged;
        UpdateTrayText();

        _ticker.Tick += OnTick;
        _ticker.Start();

        _keyboard = new KeyboardHook(_settings.Hotkeys);
        _keyboard.HotkeyPressed += OnHotkeyPressed;

        Log.Write("Iniciado");
        if (!_keyboard.IsInstalled)
        {
            Log.Write("No se pudo instalar el hook de teclado");
            _tray.ShowBalloonTip(5000, "Summs", "No se pudieron activar los atajos", ToolTipIcon.Error);
        }

        _ = DataDragon.InitAsync();
    }

    // The game's icon, embedded from Summs.ico, at the size of the tray.
    static Icon? LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("Summs.Summs.ico");
        return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
    }

    void EditHotkeys()
    {
        if (_hotkeyForm is not null)
        {
            _hotkeyForm.Activate();
            return;
        }

        // Hotkeys are ignored while the dialog is open, so pressing them to bind a key does nothing.
        using (_hotkeyForm = new HotkeyForm(_settings.Hotkeys, _icon))
        {
            try
            {
                if (_hotkeyForm.ShowDialog() != DialogResult.OK)
                    return;
                _settings.Hotkeys = _hotkeyForm.Config;
                _settings.Save();
                _keyboard.Config = _settings.Hotkeys;
                Log.Write("Atajos cambiados: " + string.Join(", ", _settings.Hotkeys.RoleKeys.Select(HotkeyConfig.KeyName))
                    + $" | {_settings.Hotkeys.Spell1} / {_settings.Hotkeys.Spell2} / {_settings.Hotkeys.Remove1} / {_settings.Hotkeys.Remove2}");
            }
            finally
            {
                _hotkeyForm = null;
            }
        }
    }

    void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Text = _paused ? "Reanudar" : "Pausar";
        UpdateTrayText();
    }

    void UpdateTrayText()
    {
        string share = !_settings.ShareTimers ? ""
            : _peers >= 2 ? $" · compartiendo con {_peers - 1}"
            : _peers == 1 ? " · sin compañeros"
            : "";
        _tray.Text = AppName + (_paused ? " - pausado" : " - activo") + share;
    }

    void ToggleShare()
    {
        _settings.ShareTimers = !_settings.ShareTimers;
        _settings.Save();
        _shareItem.Checked = _settings.ShareTimers;
        Log.Write("Compartir timers " + (_settings.ShareTimers ? "activado" : "desactivado"));
        // The next tick joins this game's room if sharing is now on.
        _roomResolved = false;
        if (!_settings.ShareTimers)
            _sync.SetRoom(null);
        UpdateTrayText();
    }

    void EditGroupKey()
    {
        if (_groupKeyForm is not null)
        {
            _groupKeyForm.Activate();
            return;
        }

        using (_groupKeyForm = new GroupKeyForm(_settings.GroupKey, _icon))
        {
            try
            {
                if (_groupKeyForm.ShowDialog() != DialogResult.OK || _groupKeyForm.GroupKey == _settings.GroupKey)
                    return;
                _settings.GroupKey = _groupKeyForm.GroupKey;
                _settings.Save();
                Log.Write(_settings.GroupKey.Length > 0 ? "Clave de grupo cambiada" : "Clave de grupo quitada");
                // The key is part of the room id: the next tick moves to the new room.
                _roomResolved = false;
            }
            finally
            {
                _groupKeyForm = null;
            }
        }
    }

    void TogglePlaceholder()
    {
        _placeholderItem.Checked = !_placeholderItem.Checked;
        _overlay.SetPlaceholder(_placeholderItem.Checked);
    }

    void SetRowStyle(RowStyle style)
    {
        _overlay.Style = style;
        _settings.RowStyle = style;
        _settings.Save();
        UpdateStyleChecks();
    }

    void UpdateStyleChecks()
    {
        _championTextItem.Checked = _overlay.Style == RowStyle.ChampionText;
        _roleIconsItem.Checked = _overlay.Style == RowStyle.RoleIcons;
    }

    async void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        if (_paused || _hotkeyForm is not null)
            return;

        if (action.IsClearAll)
        {
            _overlay.Clear();
            Log.Write("Todos los timers eliminados");
            _sync.Clear(ToShared(_gameTime ?? 0));
            return;
        }

        var (role, position) = Roles[action.Digit];
        if (action.Remove)
        {
            _overlay.RemoveTimer(role, action.Slot);
            Log.Write($"{role} hechizo {action.Slot} eliminado");
            ShareRemoval(role, action.Slot);
            return;
        }

        var data = await LiveClient.GetAllGameDataAsync();
        if (data?.GameData is null)
        {
            Log.Write($"{role}: sin partida activa");
            _tray.ShowBalloonTip(3000, "Summs", "No hay partida activa", ToolTipIcon.Warning);
            return;
        }

#if PRACTICE
        PracticeData.Apply(data);
#endif
        string? ownTeam = data.OwnTeam();
        var enemy = ownTeam is null
            ? null
            : data.AllPlayers.FirstOrDefault(p => p.Team != ownTeam && p.Position == position);
        if (enemy is null)
        {
            Log.Write($"{role}: no identificado. Equipo propio: {ownTeam ?? "?"}. Jugadores: "
                + string.Join(", ", data.AllPlayers.Select(p => $"{p.ChampionName} [{p.Team} '{p.Position}']")));
            _overlay.ShowMessage($"No se pudo identificar al {role}");
            return;
        }

        double gameTime = data.GameData.GameTime;
        var spell = enemy.SummonerSpells?.ForSlot(action.Slot);
        int? baseCooldown = spell is null ? null : Spells.BaseCooldown(spell.Id, gameTime, enemy.Level);
        if (spell is null || baseCooldown is null)
        {
            Log.Write($"{role}: hechizo {action.Slot} desconocido ({spell?.RawDisplayName ?? "ninguno"})");
            _overlay.ShowMessage($"Hechizo desconocido: {spell?.DisplayName ?? "?"}");
            return;
        }

        // The game clock going back means a new game: forget last game's answers.
        if (gameTime < _lastGameTime)
            _cosmicInsight.Clear();
        _lastGameTime = gameTime;

        int itemHaste = DataDragon.GetSummonerHaste(enemy.Items.Select(i => i.ItemId));
        // With Inspiration we assume Cosmic Insight until the user confirms it or rules it out;
        // the API doesn't say which runes they took.
        var cosmicInsight = _cosmicInsight.TryGetValue(enemy.ChampionKey, out bool hasIt)
            ? hasIt ? CosmicInsight.Confirmed : CosmicInsight.None
            : enemy.Runes?.HasInspiration == true ? CosmicInsight.Assumed : CosmicInsight.None;
        string name = Spells.IsUnleashedTeleport(spell.Id, gameTime)
            ? Spells.UnleashedTeleportName
            : DataDragon.GetName(spell.Id) ?? spell.DisplayName;

        var timer = new SpellTimer(role, action.Digit, action.Slot, enemy.ChampionKey, spell.Id, name,
            gameTime, baseCooldown.Value, itemHaste, cosmicInsight);
        _overlay.SetTimer(timer, gameTime);
        ShareTimer(timer, gameTime);
        Log.Write($"{role} {enemy.ChampionName} {name} en {Spells.FormatTime(timer.ReadyAt)} (base {baseCooldown}s, nivel {enemy.Level}, aceleración {timer.Haste}"
            + (cosmicInsight switch
            {
                CosmicInsight.Assumed => ", con Perspicacia cósmica supuesta por Inspiración)",
                CosmicInsight.Confirmed => ", con Perspicacia cósmica confirmada)",
                _ => ")",
            }));
    }

    // Checks the game once per second: advances the overlay with the game clock, clears it when
    // the game ends, and shows the "no game" note while there's none.
    async void OnTick(object? sender, EventArgs e)
    {
        if (_ticking)
            return;

        _ticking = true;
        try
        {
            double? gameTime = await LiveClient.GetGameTimeAsync();
            _overlay.SetNoGame(gameTime is null);
            if (gameTime is null)
            {
                if (_overlay.HasTimers)
                    _overlay.Clear();
                _cosmicInsight.Clear();
                _lastGameTime = 0;
                _gameTime = null;
                _roomResolved = false;
                _sync.SetRoom(null);
                return;
            }

            // The clock going back means a new game, with its own room.
            if (gameTime < _gameTime - 5)
                _roomResolved = false;
            _gameTime = gameTime;
#if PRACTICE
            _sharedOffset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - gameTime.Value;
#endif
            if (_overlay.HasTimers)
                _overlay.Tick(gameTime.Value);
            if (!_roomResolved)
                await JoinRoomAsync();
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>
    /// Joins the room of this game and team: a hash of the ten players and our team (plus the
    /// group key), which every teammate computes on their own from the game. The practice build
    /// uses a fixed test room instead, since each tester is in their own Practice Tool game.
    /// </summary>
    async Task JoinRoomAsync()
    {
        if (!_settings.ShareTimers)
        {
            _roomResolved = true;
            return;
        }

        string? room = await RoomForGameAsync();
        if (room is null)
            return; // still loading; the next tick tries again
        if (_roomResolved || !_settings.ShareTimers || _gameTime is null)
            return; // changed while waiting for the game data
        _roomResolved = true;
        _sync.SetRoom(room);

        // What we had before joining (or before turning sharing on) is shared too, unless a
        // teammate has something newer.
        foreach (var timer in _overlay.Timers.ToList())
            ShareTimer(timer, timer.PressedAt);
        foreach (var (championKey, hasIt) in _cosmicInsight)
            ShareInsight(championKey, hasIt, 0);
    }

#if PRACTICE
    Task<string?> RoomForGameAsync() =>
        Task.FromResult<string?>(SyncClient.RoomId(new[] { "summs-v1", "practica", _settings.GroupKey }));

    // Each tester is in their own Practice Tool game, with its own clock, so timers travel in
    // wall-clock seconds: this is wall clock minus game clock, as of the last tick.
    double _sharedOffset;

    double ToShared(double gameTime) => gameTime + _sharedOffset;

    double FromShared(double shared) => shared - _sharedOffset;
#else
    /// <summary>Null while the game data isn't available yet.</summary>
    async Task<string?> RoomForGameAsync()
    {
        var data = await LiveClient.GetAllGameDataAsync();
        string? ownTeam = data?.OwnTeam();
        if (data is null || ownTeam is null)
            return null;
        var players = data.AllPlayers
            .Select(p => !string.IsNullOrEmpty(p.RiotId) ? p.RiotId : p.SummonerName ?? p.ChampionName)
            .Order(StringComparer.Ordinal);
        return SyncClient.RoomId(new[] { "summs-v1", ownTeam, _settings.GroupKey }.Concat(players));
    }

    // Teammates share the game clock, so timers travel in game seconds as they are.
    static double ToShared(double gameTime) => gameTime;

    static double FromShared(double shared) => shared;
#endif

    const string InsightPrefix = "insight/";

    static string TimerKey(string role, int slot) => $"{SyncClient.TimerPrefix}{role}/{slot}";

    /// <summary>Shares a timer; stamp is the game time of the change.</summary>
    void ShareTimer(SpellTimer timer, double stamp)
    {
        var data = JsonSerializer.SerializeToElement(timer with { PressedAt = ToShared(timer.PressedAt) });
        _sync.Put(new SyncItem(TimerKey(timer.Role, timer.Slot), ToShared(stamp), data));
    }

    void ShareRemoval(string role, int slot) =>
        _sync.Put(new SyncItem(TimerKey(role, slot), ToShared(_gameTime ?? 0), null));

    void ShareInsight(string championKey, bool hasIt, double stamp) =>
        _sync.Put(new SyncItem(InsightPrefix + championKey, ToShared(stamp), JsonSerializer.SerializeToElement(hasIt)));

    /// <summary>Applies a teammate's change, after checking it's something Summs could have sent.</summary>
    void OnSyncItem(SyncItem item)
    {
        try
        {
            if (item.Key.StartsWith(SyncClient.TimerPrefix))
            {
                string[] parts = item.Key.Split('/');
                if (parts.Length != 3 || !int.TryParse(parts[2], out int slot))
                    return;
                string role = parts[1];
                if (item.Data is not { } data)
                {
                    _overlay.RemoveTimer(role, slot);
                    Log.Write($"{role} hechizo {slot} eliminado por un compañero");
                    return;
                }

                var timer = data.Deserialize<SpellTimer>();
                if (timer is null || !IsValid(timer, role, slot))
                {
                    Log.Write($"Sincronización: timer no válido en {item.Key}");
                    return;
                }
                timer = timer with { PressedAt = FromShared(timer.PressedAt) };
                _overlay.SetTimer(timer, _gameTime ?? timer.PressedAt, quiet: true);
                Log.Write($"{role} {timer.ChampionKey} {timer.SpellName} en {Spells.FormatTime(timer.ReadyAt)} (de un compañero)");
            }
            else if (item.Key.StartsWith(InsightPrefix) && item.Data is { ValueKind: JsonValueKind.True or JsonValueKind.False } answer)
            {
                string championKey = item.Key[InsightPrefix.Length..];
                if (!IsSafeKey(championKey))
                    return;
                bool hasIt = answer.GetBoolean();
                _cosmicInsight[championKey] = hasIt;
                _overlay.SetCosmicInsight(championKey, hasIt);
                Log.Write($"{championKey}: Perspicacia cósmica {(hasIt ? "confirmada" : "descartada")} por un compañero");
            }
        }
        catch (JsonException ex)
        {
            Log.Write($"Sincronización: datos no válidos en {item.Key} ({ex.Message})");
        }
    }

    // Champion and spell ids from teammates end up in icon file paths and URLs.
    static bool IsSafeKey(string? key) => key is not null && Regex.IsMatch(key, "^[A-Za-z0-9_]{1,40}$");

    static bool IsValid(SpellTimer t, string role, int slot) =>
        t.Role == role && t.Slot == slot && slot is 1 or 2
        && Roles.TryGetValue(t.Lane, out var lane) && lane.Label == role
        && IsSafeKey(t.ChampionKey) && IsSafeKey(t.SpellId)
        && t.SpellName is { Length: > 0 and <= 40 }
        && double.IsFinite(t.PressedAt)
        && t.BaseCooldown is > 0 and <= 1000 && t.ItemHaste is >= 0 and <= 300
        && Enum.IsDefined(t.CosmicInsight);

    void OnPeersChanged(int peers)
    {
        int previous = _peers;
        _peers = peers;
        UpdateTrayText();
        if (peers == previous)
            return;
        Log.Write($"Sincronización: {peers} en la sala");
        if (peers >= 2 && peers > previous)
            _overlay.ShowInfo(peers == 2 ? "Compartiendo timers con 1 compañero" : $"Compartiendo timers con {peers - 1} compañeros");
        else if (peers >= 1 && peers < previous)
            _overlay.ShowInfo("Un compañero dejó de compartir");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sync.Dispose();
            _ticker.Dispose();
            _keyboard.Dispose();
            _overlay.Dispose();
            _hotkeyForm?.Dispose();
            _groupKeyForm?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
