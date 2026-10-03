namespace Summs;

[Flags]
enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    /// <summary>Left Alt only; Right Alt is AltGr and is ignored.</summary>
    Alt = 4,
}

/// <summary>
/// User-configurable hotkeys: one key per role (TOP..SUP) and the modifiers that, combined
/// with that key, add or remove the timer of each spell slot; plus one hotkey that clears all.
/// </summary>
sealed class HotkeyConfig
{
    public static readonly string[] RoleNames = { "TOP", "JG", "MID", "ADC", "SUP" };

    public Keys[] RoleKeys { get; set; } = { Keys.NumPad7, Keys.NumPad4, Keys.NumPad5, Keys.NumPad1, Keys.NumPad2 };
    public HotkeyModifiers Spell1 { get; set; } = HotkeyModifiers.Ctrl;
    public HotkeyModifiers Spell2 { get; set; } = HotkeyModifiers.Shift;
    public HotkeyModifiers Remove1 { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;
    public HotkeyModifiers Remove2 { get; set; } = HotkeyModifiers.Shift | HotkeyModifiers.Alt;
    public Keys ClearKey { get; set; } = Keys.NumPad0;
    public HotkeyModifiers ClearModifiers { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;

    public HotkeyConfig Clone() => new()
    {
        RoleKeys = (Keys[])RoleKeys.Clone(),
        Spell1 = Spell1,
        Spell2 = Spell2,
        Remove1 = Remove1,
        Remove2 = Remove2,
        ClearKey = ClearKey,
        ClearModifiers = ClearModifiers,
    };

    /// <summary>Returns why the configuration can't be used, or null if it's fine.</summary>
    public string? Validate()
    {
        if (RoleKeys is null || RoleKeys.Length != RoleNames.Length || RoleKeys.Any(k => k == Keys.None))
            return "Falta la tecla de algún rol.";
        if (RoleKeys.Distinct().Count() != RoleKeys.Length)
            return "Dos roles usan la misma tecla.";
        var modifiers = new[] { Spell1, Spell2, Remove1, Remove2 };
        if (modifiers.Distinct().Count() != modifiers.Length)
            return "Dos acciones usan los mismos modificadores.";
        if (ClearKey == Keys.None)
            return "Falta la tecla de borrar todo.";
        if (RoleKeys.Contains(ClearKey) && modifiers.Contains(ClearModifiers))
            return "El atajo de borrar todo coincide con el de un rol.";
        return null;
    }

    /// <summary>The action for a role key pressed with exactly these modifiers, if any.</summary>
    public HotkeyAction? Match(Keys key, HotkeyModifiers modifiers)
    {
        if (key == ClearKey && modifiers == ClearModifiers)
            return HotkeyAction.ClearAll;
        int role = Array.IndexOf(RoleKeys, key) + 1;
        if (role == 0)
            return null;
        if (modifiers == Spell1) return new HotkeyAction(role, 1, false);
        if (modifiers == Spell2) return new HotkeyAction(role, 2, false);
        if (modifiers == Remove1) return new HotkeyAction(role, 1, true);
        if (modifiers == Remove2) return new HotkeyAction(role, 2, true);
        return null;
    }

    // Numpad keys by scan code. With Num Lock on, Shift+Num4 is reported as VK_LEFT (and with
    // Num Lock off every numpad key is an arrow/Home/End...), so they're identified by scan code
    // without the extended flag, which tells them apart from the real arrow keys.
    static readonly Dictionary<uint, Keys> NumpadScanCodes = new()
    {
        [0x52] = Keys.NumPad0, [0x4F] = Keys.NumPad1, [0x50] = Keys.NumPad2, [0x51] = Keys.NumPad3,
        [0x4B] = Keys.NumPad4, [0x4C] = Keys.NumPad5, [0x4D] = Keys.NumPad6, [0x47] = Keys.NumPad7,
        [0x48] = Keys.NumPad8, [0x49] = Keys.NumPad9, [0x53] = Keys.Decimal,
    };

    /// <summary>The key to bind for a key event, independent of Num Lock and Shift.</summary>
    public static Keys Normalize(uint vkCode, uint scanCode, bool extended) =>
        !extended && NumpadScanCodes.TryGetValue(scanCode, out var numpad) ? numpad : (Keys)vkCode;

    public static bool IsModifierKey(Keys key) => key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
        or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    public static string KeyName(Keys key) => key switch
    {
        Keys.None => "",
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (key - Keys.NumPad0),
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        Keys.Decimal => "Num.",
        Keys.Add => "Num+",
        Keys.Subtract => "Num-",
        Keys.Multiply => "Num*",
        Keys.Divide => "Num/",
        _ => key.ToString(),
    };

    /// <summary>e.g. "Ctrl+Alt+Num0", or just the key without modifiers.</summary>
    public static string ComboName(HotkeyModifiers modifiers, Keys key) =>
        modifiers == HotkeyModifiers.None ? KeyName(key) : ModifiersName(modifiers) + "+" + KeyName(key);

    public static string ModifiersName(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        return parts.Count == 0 ? "Ninguno" : string.Join("+", parts);
    }
}
