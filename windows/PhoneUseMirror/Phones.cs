using System.Reflection;
using System.Text.Json.Nodes;

namespace PhoneUseMirror;

/// <summary>
/// The phone frames' table, phoneuse/web/phones.json, built into this app (PhoneUseMirror.csproj):
/// the same file the browser mirror draws its frame from. PhoneModel and FrameFinish read it.
/// </summary>
static class Phones
{
    public static readonly JsonObject Json = Load();

    static JsonObject Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("phones.json")
            ?? throw new InvalidOperationException("phones.json isn't built into the app");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("phones.json: not an object");
    }

    public static double Num(JsonNode? n) => n!.GetValue<double>();
    public static string? Str(JsonNode? n) => n?.GetValue<string>();
}
