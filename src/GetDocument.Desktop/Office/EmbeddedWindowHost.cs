using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace GetDocument.Desktop.Office;

/// <summary>
/// Hosts another process's top-level window (the LibreOffice document window) inside an
/// Avalonia control. Our own Win32 child window is the stable parent: when the tab is hidden
/// Avalonia destroys the native control, so the window is parked on a hidden owner instead of
/// destroyed, and comes back intact when the tab is shown again.
/// </summary>
internal sealed class EmbeddedWindowHost : NativeControlHost
{
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_APPWINDOW = 0x00040000, WS_EX_WINDOWEDGE = 0x00000100, WS_EX_CLIENTEDGE = 0x00000200;
    private static IntPtr _parking;

    private IntPtr _host;
    private IntPtr _child;

    public EmbeddedWindowHost()
    {
        // Native child windows paint over Avalonia; keep the size in step on every layout pass.
        LayoutUpdated += (_, _) => Fit();
    }

    /// <summary>True once Avalonia has created the native host window.</summary>
    public bool IsReady => _host != IntPtr.Zero;

    /// <summary>The embedded window (0 until <see cref="Embed"/>).</summary>
    public IntPtr Child => _child;

    /// <summary>Finds a visible-or-hidden top-level window whose title contains <paramref name="marker"/>.</summary>
    public static IntPtr FindWindow(string marker)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            int length = GetWindowTextLength(window);
            if (length == 0) return true;
            var text = new StringBuilder(length + 1);
            GetWindowText(window, text, text.Capacity);
            if (text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Makes <paramref name="window"/> a borderless child filling this control.</summary>
    public void Embed(IntPtr window)
    {
        if (_host == IntPtr.Zero) throw new InvalidOperationException("Область редактора ещё не создана.");
        _child = window;
        int style = GetWindowLong(window, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | unchecked((int)WS_POPUP));
        SetWindowLong(window, GWL_STYLE, style | WS_CHILD);
        int extended = GetWindowLong(window, GWL_EXSTYLE) & ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE);
        SetWindowLong(window, GWL_EXSTYLE, extended);   // no taskbar button for the document window
        SetParent(window, _host);
        Fit();
    }

    /// <summary>Gives keyboard focus to the embedded window (Windows attaches the input queues).</summary>
    public void FocusChild()
    {
        if (_child != IntPtr.Zero) SetFocus(_child);
    }

    public void Fit()
    {
        if (_host == IntPtr.Zero || _child == IntPtr.Zero) return;
        // LibreOffice on Windows draws its menu bar as a native window menu; the document
        // window must not carry one (GET Document's toolbar replaces it). It may be re-attached
        // when the window is shown, so check on every fit.
        if (GetMenu(_child) != IntPtr.Zero)
        {
            SetMenu(_child, IntPtr.Zero);
            DrawMenuBar(_child);
        }
        if (!GetClientRect(_host, out var area)) return;
        MoveWindow(_child, 0, 0, Math.Max(1, area.Right - area.Left), Math.Max(1, area.Bottom - area.Top), true);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);
        if (_host == IntPtr.Zero)
        {
            _host = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_host == IntPtr.Zero) return base.CreateNativeControlCore(parent);
        }
        else
        {
            SetParent(_host, parent.Handle);
        }
        Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        return new PlatformHandle(_host, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows() || control.Handle != _host)
        {
            base.DestroyNativeControlCore(control);
            return;
        }
        // Tab switched away: keep the document window alive on the hidden parking window.
        SetParent(_host, Parking());
    }

    /// <summary>Releases the host window; the caller has already closed the embedded document.</summary>
    public void Release()
    {
        if (_host != IntPtr.Zero && OperatingSystem.IsWindows()) DestroyWindow(_host);
        _host = IntPtr.Zero;
        _child = IntPtr.Zero;
    }

    private static IntPtr Parking()
    {
        if (_parking == IntPtr.Zero)
            _parking = CreateWindowEx(0, "Static", "GET Document parking", unchecked((int)WS_POPUP), 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _parking;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr window, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DrawMenuBar(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
}
