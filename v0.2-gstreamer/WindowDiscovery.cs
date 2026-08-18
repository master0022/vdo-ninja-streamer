using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace StreamerV2;

public static class WindowDiscovery
{
    private static readonly string[] IgnoredTitleFragments =
    [
        "Program Manager", "NVIDIA GeForce Overlay", "GeForce Overlay",
        "Microsoft Text Input Application", "Settings", "Windows Input Experience",
        "Xbox Game Bar", "Game Bar", "Snipping Tool"
    ];

    public static IReadOnlyList<WindowTarget> List(nint ignoredHwnd = 0)
    {
        var result = new List<WindowTarget>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == ignoredHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            var title = GetWindowText(hwnd);
            if (string.IsNullOrWhiteSpace(title) || IgnoredTitleFragments.Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return true;

            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { return true; }

            var className = GetClassName(hwnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd")
                return true;

            result.Add(new WindowTarget(hwnd, (int)pid, title, processName, className));
            return true;
        }, nint.Zero);

        return result.OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<WindowTarget> ListMonitors()
    {
        return Screen.AllScreens
            .Select((screen, index) => new WindowTarget(
                0,
                0,
                $"Monitor {index + 1} · {screen.DeviceName} · {screen.Bounds.Width}x{screen.Bounds.Height}" +
                (screen.Primary ? " · Primary" : ""),
                "monitor",
                "MONITOR",
                VideoSourceKind.Monitor,
                index))
            .ToArray();
    }

    public static bool IsAlive(WindowTarget target) =>
        target.SourceKind == VideoSourceKind.Monitor
            ? target.MonitorIndex >= 0 && target.MonitorIndex < Screen.AllScreens.Length
            : IsWindow(target.Hwnd) && !IsIconic(target.Hwnd);

    public static int? FindDiscordRootPid()
    {
        var candidates = new List<(int Pid, DateTime Started)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (IsDiscordProcessName(process.ProcessName))
                {
                    DateTime started;
                    try { started = process.StartTime; }
                    catch { started = DateTime.MaxValue; }
                    candidates.Add((process.Id, started));
                }
            }
            catch
            {
                // Processes can disappear while the snapshot is being read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Started)
            .Select(candidate => (int?)candidate.Pid)
            .FirstOrDefault();
    }

    private static bool IsDiscordProcessName(string processName)
    {
        var normalized = new string(processName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "discord" or "discordptb" or "discordcanary" or "discorddevelopment" or "discordapp" ||
               normalized.StartsWith("discordhelper", StringComparison.Ordinal);
    }

    private static string GetWindowText(nint hwnd)
    {
        var text = new StringBuilder(1024);
        _ = GetWindowText(hwnd, text, text.Capacity);
        return text.ToString().Trim();
    }

    private static string GetClassName(nint hwnd)
    {
        var text = new StringBuilder(256);
        _ = GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
