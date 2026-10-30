using System.Diagnostics;
using System.Text;

namespace CropPipViewer;

public static class WindowService
{
    public static List<WindowInfo> EnumerateWindows()
    {
        var candidates = new List<WindowCandidate>();
        var ownPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd) || !NativeMethods.IsWindowVisible(hWnd))
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var rawPid);
            var pid = (int)rawPid;
            if (pid <= 0 || pid == ownPid) return true;

            var len = NativeMethods.GetWindowTextLength(hWnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return true;

            var processName = TryGetProcessName(pid);
            var isMapleProcess = processName.Contains("MapleStory", StringComparison.OrdinalIgnoreCase);
            var isMapleTitle = title.Contains("MapleStory", StringComparison.OrdinalIgnoreCase);

            // This selector is only for the MapleStory WGC source. Do not expose arbitrary
            // desktop/app/PiP windows here; they are not valid targets for this workflow.
            if (!isMapleProcess && !isMapleTitle) return true;

            var clientArea = 0L;
            if (NativeMethods.GetClientRect(hWnd, out var client))
            {
                var width = Math.Max(0, client.Width);
                var height = Math.Max(0, client.Height);
                clientArea = (long)width * height;
            }

            var mainWindowHandle = TryGetMainWindowHandle(pid);
            long score = clientArea;
            if (hWnd == mainWindowHandle) score += 1_000_000_000_000L;
            if (string.Equals(title, "MapleStory", StringComparison.OrdinalIgnoreCase)) score += 1_000_000_000L;
            if (isMapleProcess) score += 100_000_000L;

            candidates.Add(new WindowCandidate(
                new WindowInfo { Hwnd = hWnd, Title = title, ProcessId = pid },
                score));

            return true;
        }, IntPtr.Zero);

        // MapleStory can expose more than one visible top-level HWND for the same process.
        // Keep only the best representative window per PID (prefer Process.MainWindowHandle,
        // then the exact MapleStory title, then the largest client area).
        return candidates
            .GroupBy(c => c.Info.ProcessId)
            .Select(g => g.OrderByDescending(c => c.Score).First().Info)
            .OrderBy(w => w.ProcessId)
            .ToList();
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IntPtr TryGetMainWindowHandle(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static bool TryResolveLiveWindow(WindowInfo selected, out WindowInfo live)
    {
        live = selected;
        if (selected.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(selected.Hwnd))
        {
            NativeMethods.GetWindowThreadProcessId(selected.Hwnd, out var currentPid);
            if ((int)currentPid == selected.ProcessId)
            {
                return true;
            }
        }

        var windows = EnumerateWindows();
        live = windows.FirstOrDefault(w =>
                   w.ProcessId == selected.ProcessId &&
                   string.Equals(w.Title, selected.Title, StringComparison.Ordinal))
               ?? windows.FirstOrDefault(w =>
                   w.ProcessId == selected.ProcessId)
               ?? windows.FirstOrDefault()
               ?? selected;

        return live.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(live.Hwnd);
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

    private sealed record WindowCandidate(WindowInfo Info, long Score);
}
