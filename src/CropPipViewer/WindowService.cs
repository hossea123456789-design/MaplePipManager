using System.Text;

namespace CropPipViewer;

public static class WindowService
{
    public static List<WindowInfo> EnumerateWindows()
    {
        var list = new List<WindowInfo>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            var len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            list.Add(new WindowInfo { Hwnd = hWnd, Title = title, ProcessId = (int)pid });
            return true;
        }, IntPtr.Zero);

        return list.OrderByDescending(w => w.Title.Contains("MapleStory", StringComparison.OrdinalIgnoreCase))
                   .ThenBy(w => w.Title)
                   .ToList();
    }

    public static bool TryGetClientScreenRect(IntPtr hwnd, out RectI rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetClientRect(hwnd, out var client)) return false;
        var pt = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref pt)) return false;
        rect = new RectI(pt.X, pt.Y, client.Width, client.Height);
        return client.Width > 0 && client.Height > 0;
    }

    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);
}
