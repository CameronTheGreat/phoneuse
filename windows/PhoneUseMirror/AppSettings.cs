using System.Text.Json.Nodes;

namespace PhoneUseMirror;

/// <summary>
/// This window's own settings (not phoneuse's, which the server keeps):
/// %LOCALAPPDATA%\phoneuse\PhoneUseMirror.json.
/// </summary>
public static class AppSettings
{
    static readonly string FilePath = Backend.Portable
        ? Path.Combine(Backend.PortableData, "PhoneUseMirror.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "phoneuse", "PhoneUseMirror.json");

    static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { return new JsonObject(); }
    }

    static void Save(JsonObject o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString());
        }
        catch (Exception) { }
    }

    /// <summary>The first-time setup checklist was finished or skipped: it no longer opens by itself.</summary>
    public static bool SetupDone
    {
        get => (bool?)Load()["setupDone"] ?? false;
        set
        {
            var o = Load();
            o["setupDone"] = value;
            Save(o);
        }
    }

    /// <summary>What setup was for (the first question it asks): "desktop" (the phone from this PC) or
    /// "agent" (and AI agents too, which adds the Agent setup stage); null until chosen.</summary>
    public static string? SetupMode
    {
        get => (string?)Load()["setupMode"];
        set => Set("setupMode", value);
    }

    /// <summary>Setup's last screen turned Start with Windows on once (the agent path): it doesn't again.</summary>
    public static bool StartupOffered
    {
        get => (bool?)Load()["startupOffered"] ?? false;
        set => Set("startupOffered", value);
    }

    /// <summary>Look for a newer release on GitHub now and then (Updater).</summary>
    public static bool AutoUpdate
    {
        get => (bool?)Load()["autoUpdate"] ?? true;
        set
        {
            var o = Load();
            o["autoUpdate"] = value;
            Save(o);
        }
    }

    /// <summary>The release whose "update available" notice was closed: it isn't shown again for that one.</summary>
    public static string? UpdateDismissed
    {
        get => (string?)Load()["updateDismissed"];
        set
        {
            var o = Load();
            o["updateDismissed"] = value;
            Save(o);
        }
    }

    /// <summary>The window stays above other apps' windows (MainWindow.Snap.cs); on by default.</summary>
    public static bool AlwaysOnTop
    {
        get => (bool?)Load()["alwaysOnTop"] ?? true;
        set => Set("alwaysOnTop", value);
    }

    /// <summary>The window opens above the tray icon and snaps back there when dropped near it
    /// (MainWindow.Snap.cs); on by default. Off: no snapping at all, it stays where it's put.</summary>
    public static bool KeepAboveTray
    {
        get => (bool?)Load()["keepAboveTray"] ?? true;
        set => Set("keepAboveTray", value);
    }

    /// <summary>Where it's pinned, in DIPs: along the taskbar from the icon's centre.</summary>
    public static double SnapAlong
    {
        get => Number("snapAlong") ?? 0;
        set => Set("snapAlong", value);
    }

    /// <summary>Where it's pinned, in DIPs: away from the taskbar. None yet: the default.</summary>
    public static double? SnapGap
    {
        get => Number("snapGap");
        set => Set("snapGap", value);
    }

    /// <summary>Where it was last left when not pinned (physical pixels).</summary>
    public static Windows.Graphics.PointInt32? FloatPosition
    {
        get => Number("floatX") is { } x && Number("floatY") is { } y ? new((int)x, (int)y) : null;
        set
        {
            var o = Load();
            o["floatX"] = value?.X;
            o["floatY"] = value?.Y;
            Save(o);
        }
    }

    static double? Number(string key)
    {
        try { return (double?)Load()[key]; }
        catch (Exception) { return null; }
    }

    static void Set(string key, JsonNode? value)
    {
        var o = Load();
        o[key] = value;
        Save(o);
    }

    /// <summary>The dark palette for the mirror's app screens. Existing installs start in light mode.</summary>
    public static bool DarkMode
    {
        get => Load()["darkMode"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
        set => Set("darkMode", value);
    }

    /// <summary>The activity sidebar beside the phone (ActivitySidebar) is open; closed by default.</summary>
    public static bool ActivityOpen
    {
        get => (bool?)Load()["activityOpen"] ?? false;
        set => Set("activityOpen", value);
    }

    /// <summary>The agent glow around the phone (AgentGlow); on by default.</summary>
    public static bool GlowOn
    {
        get => (bool?)Load()["glowOn"] ?? true;
        set => Set("glowOn", value);
    }

    public static readonly GlowLook DefaultGlowLook = new(1f, 1f, 1.3f, true, 2.5f);

    /// <summary>The Aurora glow's tuning (Settings › Agent glow).</summary>
    public static GlowLook GlowLook
    {
        get
        {
            var d = DefaultGlowLook;
            if (Load()["glow"] is not JsonObject g) return d;
            try
            {
                return new GlowLook(
                    Math.Clamp((float?)g["intensity"] ?? d.Intensity, 0.2f, 1.6f),
                    Math.Clamp((float?)g["speed"] ?? d.Speed, 0.2f, 3f),
                    Math.Clamp((float?)g["size"] ?? d.Size, 0.3f, 2.5f),
                    (bool?)g["bleed"] ?? d.Bleed,
                    Math.Clamp((float?)g["hdrPeak"] ?? d.HdrPeak, 1f, 4f));
            }
            catch (Exception) { return d; }
        }
        set => Set("glow", new JsonObject
        {
            ["intensity"] = Math.Round(value.Intensity, 2),
            ["speed"] = Math.Round(value.Speed, 2),
            ["size"] = Math.Round(value.Size, 2),
            ["bleed"] = value.Bleed,
            ["hdrPeak"] = Math.Round(value.HdrPeak, 2),
        });
    }

    /// <summary>The phone frame's colour (Settings › Appearance): a FrameFinish id.</summary>
    public static string FrameFinish
    {
        get => (string?)Load()["frameFinish"] ?? PhoneUseMirror.FrameFinish.DefaultId;
        set => Set("frameFinish", value);
    }

    /// <summary>The setup checklist's "Turn on AssistiveTouch" step was ticked off by hand.</summary>
    public static bool AssistiveTouchDone
    {
        get => (bool?)Load()["assistiveTouchDone"] ?? false;
        set
        {
            var o = Load();
            o["assistiveTouchDone"] = value;
            Save(o);
        }
    }
}
