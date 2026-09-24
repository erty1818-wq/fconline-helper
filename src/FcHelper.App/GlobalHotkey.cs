using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FcHelper.App;

/// <summary>
/// System-wide shortcut through RegisterHotKey. Deliberately not a low-level keyboard hook: hooks see every
/// key press (key-logger territory) and game anti-cheat software may object to them.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private static int _nextId = 0x5100;

    private readonly HwndSource _window;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private readonly Action _onPressed;

    public bool IsRegistered { get; }

    public GlobalHotkey(uint modifiers, uint virtualKey, Action onPressed)
    {
        _onPressed = onPressed;
        // An invisible window (no WS_VISIBLE style) that only exists to receive WM_HOTKEY.
        _window = new HwndSource(0, 0, 0, 0, 0, "FcHelperHotkey", IntPtr.Zero);
        _window.AddHook(WndProc);
        IsRegistered = RegisterHotKey(_window.Handle, _id, modifiers | ModNoRepeat, virtualKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            _onPressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(_window.Handle, _id);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
