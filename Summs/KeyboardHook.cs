using System.Runtime.InteropServices;

namespace Summs;

/// <summary>
/// Which role (1-5, TOP..SUP) and spell slot a hotkey refers to, and whether it adds or
/// removes the timer; or, with Digit 0, that every timer is cleared.
/// </summary>
readonly record struct HotkeyAction(int Digit, int Slot, bool Remove)
{
    public static readonly HotkeyAction ClearAll = new(0, 0, true);

    public bool IsClearAll => Digit == 0;
}

/// <summary>
/// Low-level keyboard hook for the hotkeys in <see cref="Config"/>.
/// RegisterHotKey is not used because games can disable it (RIDEV_NOHOTKEYS) while focused.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const uint LLKHF_EXTENDED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    readonly LowLevelKeyboardProc _proc; // Kept in a field so the GC doesn't collect it.
    readonly IntPtr _hook;
    readonly HashSet<Keys> _modifiers = new();
    readonly HashSet<Keys> _heldKeys = new();

    public HotkeyConfig Config { get; set; }

    public event EventHandler<HotkeyAction>? HotkeyPressed;

    public KeyboardHook(HotkeyConfig config)
    {
        Config = config;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = wParam.ToInt32() is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool extended = (info.Flags & LLKHF_EXTENDED) != 0;
            var key = (Keys)info.VkCode;

            if (IsModifier(key))
            {
                // The Shift release Windows fakes around Shift+numpad (Num Lock on) carries
                // scan code 0x22A, or 0x2A flagged as extended depending on the source.
                bool fakeShift = key is Keys.LShiftKey or Keys.RShiftKey
                    && (extended || (info.ScanCode & 0x200) != 0);
                if (!fakeShift)
                {
                    if (down)
                        _modifiers.Add(key);
                    else
                        _modifiers.Remove(key);
                }
            }
            else
            {
                var normalized = HotkeyConfig.Normalize(info.VkCode, info.ScanCode, extended);
                if (!down)
                    _heldKeys.Remove(normalized);
                else if (_heldKeys.Add(normalized)) // Ignore auto-repeat while held.
                    OnKey(normalized);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static bool IsModifier(Keys key) => key is Keys.LControlKey or Keys.RControlKey
        or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu;

    void OnKey(Keys key)
    {
        // Right Alt is AltGr (reported as Ctrl+Alt), so any combination with it is ignored.
        if (_modifiers.Contains(Keys.RMenu))
            return;

        var modifiers = HotkeyModifiers.None;
        if (_modifiers.Contains(Keys.LControlKey) || _modifiers.Contains(Keys.RControlKey))
            modifiers |= HotkeyModifiers.Ctrl;
        if (_modifiers.Contains(Keys.LShiftKey) || _modifiers.Contains(Keys.RShiftKey))
            modifiers |= HotkeyModifiers.Shift;
        if (_modifiers.Contains(Keys.LMenu))
            modifiers |= HotkeyModifiers.Alt;

        var action = Config.Match(key, modifiers);
        if (action is not null)
            HotkeyPressed?.Invoke(this, action.Value);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
    }
}
