using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FcHelper.Vision;

/// <summary>
/// The game's window, found by process name through the OS window list. Nothing touches the game process
/// itself: no handle to its memory, no injection, only window position and size from user32.
/// </summary>
public sealed class GameWindow
{
    /// <summary>FC Online's process (window title "FC ONLINE"). Kept overridable in case the client changes.</summary>
    public const string DefaultProcessName = "fczf";

    public IntPtr Handle { get; }

    private GameWindow(IntPtr handle) => Handle = handle;

    public static GameWindow? Find(string processName = DefaultProcessName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (p.MainWindowHandle != IntPtr.Zero) return new GameWindow(p.MainWindowHandle);
            }
        }
        return null;
    }

    public bool IsForeground => GetForegroundWindow() == Handle;
    public bool IsMinimized => IsIconic(Handle);

    /// <summary>
    /// Puts one of this app's windows directly under the game in the window order: over everything else, never over the
    /// game. Only our window moves and nothing is activated. A window shown without focus still opens on top of the others.
    /// </summary>
    public void PlaceUnder(IntPtr ownWindow) => SetWindowPos(ownWindow, Handle, 0, 0, 0, 0, NoSize | NoMove | NoActivate);

    /// <summary>The game image in screen pixels, without the title bar and border of windowed mode.</summary>
    public Rectangle? ClientBounds
    {
        get
        {
            if (!GetClientRect(Handle, out var r) || r.Right <= 0 || r.Bottom <= 0) return null;
            var origin = new POINT();
            if (!ClientToScreen(Handle, ref origin)) return null;
            return new Rectangle(origin.X, origin.Y, r.Right, r.Bottom);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
