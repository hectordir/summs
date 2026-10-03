using System.Runtime.InteropServices;

namespace Summs;

/// <summary>
/// Low-level mouse hook that hands the wheel over a region to Summs and keeps it from the game,
/// so scrolling over the overlay doesn't also zoom the camera.
/// </summary>
sealed class WheelHook : IDisposable
{
    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    readonly LowLevelMouseProc _proc; // Kept in a field so the GC doesn't collect it.
    readonly IntPtr _hook;
    readonly Func<bool> _captures;
    readonly Action<int> _onWheel;
    readonly SynchronizationContext _context;

    /// <param name="captures">
    /// Whether a wheel event now is for Summs. It should check <see cref="Cursor.Position"/>
    /// rather than the hook's point, which is in physical pixels when Windows scales the display.
    /// </param>
    /// <param name="onWheel">Called later on the UI thread with the wheel delta.</param>
    public WheelHook(Func<bool> captures, Action<int> onWheel)
    {
        _captures = captures;
        _onWheel = onWheel;
        _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Write($"No se pudo instalar el hook del ratón (error {Marshal.GetLastWin32Error()})");
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
        {
            if (_captures())
            {
                // MSLLHOOKSTRUCT.mouseData, after the point: the wheel delta in its high word.
                int delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                // Low-level hooks must return quickly, so the work happens after returning.
                _context.Post(_ => _onWheel(delta), null);
                return new IntPtr(1); // Swallowed: the game never sees it.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
    }
}
