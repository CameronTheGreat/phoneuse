using System.Diagnostics;
using System.Net.Sockets;

namespace PhoneUseMirror;

/// <summary>
/// phoneuse itself stays what it is: the checkout's Python (bin\phoneuse, .venv),
/// started and stopped on its own. The window only starts it when nothing answers
/// on the mirror's port: `python -m phoneuse.windows.background`, which is
/// `phoneuse serve` with no console, logging to phoneuse-up.log. Quit stops that
/// background copy (by the pid it writes); a `phoneuse up` running in a terminal is
/// left alone.
/// </summary>
public static class Backend
{
    /// <summary>The checkout this window was built in (winui\dist\ is two levels down).</summary>
    public static string? Root { get; } = FindRoot();

    static string Runtime => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "phoneuse", "run");

    public static string LogPath => Path.Combine(Runtime, "phoneuse-up.log");
    static string PidPath => Path.Combine(Runtime, "phoneuse-background.pid");

    static string? FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "bin", "phoneuse")) &&
                File.Exists(Path.Combine(d.FullName, ".venv", "Scripts", "python.exe")))
                return d.FullName;
        return null;
    }

    public static bool Answers(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(400) && c.Connected;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Start phoneuse in the background unless something already serves the port.
    /// Returns a line for the log/tooltip.</summary>
    public static string EnsureRunning(int port)
    {
        if (Answers(port)) return "phoneuse is already running";
        if (Background() != null) return "phoneuse is starting";
        if (Root == null) return "phoneuse not found next to this app: run `phoneuse up`";
        var psi = new ProcessStartInfo(Path.Combine(Root, ".venv", "Scripts", "python.exe"),
                                       "-u -m phoneuse.windows.background")
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUTF8"] = "1";
        Process.Start(psi);
        return "starting phoneuse";
    }

    /// <summary>The background phoneuse this window (or an earlier one) started, if it's running.</summary>
    public static Process? Background()
    {
        try
        {
            var p = Process.GetProcessById(int.Parse(File.ReadAllText(PidPath).Trim()));
            return p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
        catch (Exception) { return null; }
    }

    public static bool OwnsRunning => Background() != null;

    /// <summary>Stop the background phoneuse. Its services are in its job object and stop
    /// with it; the Bluetooth HID service keeps running, as after `phoneuse up`.</summary>
    public static void Stop()
    {
        var p = Background();
        if (p == null) return;
        try
        {
            p.Kill();
            p.WaitForExit(3000);
        }
        catch (Exception) { }
        try { File.Delete(PidPath); } catch (Exception) { }
    }
}
