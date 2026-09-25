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

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
