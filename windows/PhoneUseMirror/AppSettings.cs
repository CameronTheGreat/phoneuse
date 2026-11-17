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

    /// <summary>"Agent control requires pairing" was closed: it stays gone for good.</summary>
    public static bool PairingBarDismissed
    {
        get => (bool?)Load()["pairingBarDismissed"] ?? false;
        set
        {
            var o = Load();
            o["pairingBarDismissed"] = value;
            Save(o);
        }
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
