using System.IO;
using System.Text.Json;

namespace DiagScope2;

internal static class DtcTranslations
{
    static readonly Lazy<Dictionary<string, string>> Cache = new(ReadCache);

    static Dictionary<string, string> ReadCache()
    {
        try
        {
            string path = Path.Combine(Store.Root, "DtcTranslations.json");
            return new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string Local(string description) =>
        Cache.Value.TryGetValue(description, out var result) ? result ?? "" : "";
}
