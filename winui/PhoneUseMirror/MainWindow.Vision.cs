using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhoneUseMirror;

/// <summary>
/// Settings › Screen reading: the local models that read the phone's screen
/// (RapidOCR and GoClick, scripts\windows\setup-vision.ps1, in the checkout's
/// .venv-vision). Agents need them to read text, tap labels, type and open apps by
/// name. Install runs the setup script in the background (its output goes to
/// phoneuse-vision-setup.log); Remove stops the OCR service and deletes .venv-vision.
/// The downloaded grounding model stays in the Hugging Face cache, which other
/// copies of phoneuse share.
/// </summary>
public sealed partial class MainWindow
{
    enum VisionJob { None, Installing, Removing }
    VisionJob _visionJob;
    string _visionStep = "";
    string? _visionError;

    static string? VisionVenv => Backend.Root == null ? null : Path.Combine(Backend.Root, ".venv-vision");

    static bool VisionInstalled =>
        VisionVenv is string v && File.Exists(Path.Combine(v, "Scripts", "python.exe")) &&
        Directory.Exists(Path.Combine(v, "Lib", "site-packages", "rapidocr"));

    static string VisionLog => Path.Combine(Path.GetDirectoryName(Backend.LogPath)!, "phoneuse-vision-setup.log");

    void ShowVision()
    {
        bool busy = _visionJob != VisionJob.None;
        VisionRing.IsActive = busy;
        VisionRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VisionButton.IsEnabled = !busy && Backend.Root != null;
        if (Backend.Root == null)
        {
            VisionText.Text = "phoneuse's folder wasn't found next to this app.";
            return;
        }
        if (_visionJob == VisionJob.Installing)
        {
            VisionText.Text = "Installing (several GB, can take a while)… " + _visionStep;
            VisionButton.Content = "Installing";
            return;
        }
        if (_visionJob == VisionJob.Removing)
        {
            VisionText.Text = "Removing…";
            VisionButton.Content = "Removing";
            return;
        }
        bool on = VisionInstalled;
        VisionText.Text = (_visionError != null ? _visionError + " " : "") + (on
            ? "Installed. Agents can read the screen, tap text, type and open apps by name."
            : "Not installed. Agents can only use screenshots: no reading text, typing or opening apps by name.");
        VisionButton.Content = on ? "Remove" : "Install";
    }

    async void Vision_Click(object sender, RoutedEventArgs e)
    {
        if (_visionJob != VisionJob.None || Backend.Root == null) return;
        _visionError = null;
        if (VisionInstalled) await RemoveVision();
        else await InstallVision();
    }

    async Task InstallVision()
    {
        _visionJob = VisionJob.Installing;
        _visionStep = "";
        ShowVision();
        int code;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(Backend.Root!, "scripts", "windows", "setup-vision.ps1")}\"")
            {
                WorkingDirectory = Backend.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var log = new StreamWriter(VisionLog, append: true) { AutoFlush = true };
            log.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} install");
            using var p = Process.Start(psi)!;
            void Line(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                lock (log) log.WriteLine(s);
                var step = s.Trim().TrimStart('=', '>', ' ');
                _ui.TryEnqueue(() => { _visionStep = step; if (_visionJob == VisionJob.Installing) ShowVision(); });
            }
            p.OutputDataReceived += (_, a) => Line(a.Data);
            p.ErrorDataReceived += (_, a) => Line(a.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            code = p.ExitCode;
        }
        catch (Exception ex)
        {
            code = -1;
            _visionStep = ex.Message;
        }
        _visionJob = VisionJob.None;
        if (code != 0 || !VisionInstalled)
            _visionError = "The install failed (the tray's log folder has phoneuse-vision-setup.log).";
        ShowVision();
        if (_visionError == null)
            _tray?.Balloon("Screen reading installed", "Agents can read the phone's screen, type and open apps by name.");
    }

    async Task RemoveVision()
    {
        var confirm = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Remove screen reading?",
            Content = "Agents won't be able to read the screen, type or open apps by name until you install it again " +
                      "(a download of several GB).",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        SettingsFlyout.Hide();
        if (_ask != null) return;
        try { if (await confirm.ShowAsync() != ContentDialogResult.Primary) return; }
        catch (Exception) { return; }

        _visionJob = VisionJob.Removing;
        ShowVision();
        var root = Backend.Root!;
        var venv = VisionVenv!;
        string? error = await Task.Run(() =>
        {
            try
            {
                // The OCR service runs from .venv-vision: ask it to quit before deleting its files.
                var stop = Process.Start(new ProcessStartInfo(Path.Combine(root, ".venv", "Scripts", "python.exe"),
                                                              "-c \"from phoneuse.cli import stop_vision; stop_vision()\"")
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                });
                stop?.WaitForExit(10000);
                for (int i = 0; ; i++)
                {
                    try
                    {
                        if (Directory.Exists(venv)) Directory.Delete(venv, true);
                        return null;
                    }
                    catch (Exception) when (i < 5) { Thread.Sleep(1000); }  // files still closing
                }
            }
            catch (Exception ex) { return "Couldn't remove it all (" + ex.Message + ")."; }
        });
        _visionJob = VisionJob.None;
        _visionError = error;
        ShowVision();
    }
}
