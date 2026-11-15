using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiagScope2;

internal static class DtcTranslations
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    static readonly SemaphoreSlim Requests = new(1);
    static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, DateTimeOffset> RetryAfter = new(StringComparer.OrdinalIgnoreCase);
    static readonly object CacheLock = new();
    static readonly string CachePath = Path.Combine(Store.Root, "DtcTranslations.json");
    static readonly Lazy<Dictionary<string, string>> Cache = new(ReadCache);
    static DateTimeOffset serviceRetryAfter;

    static Dictionary<string, string> ReadCache()
    {
        try
        {
            return new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string Local(string description)
    {
        lock (CacheLock)
            return Cache.Value.TryGetValue(description, out var result) ? result ?? "" : "";
    }

    internal static async Task<string> TranslateAsync(string description)
    {
        string local = Local(description);
        if (local.Length > 0 || !IsShareableDescription(description)) return local;

        var operation = Pending.GetOrAdd(description, value => new Lazy<Task<string>>(() => DownloadAsync(value)));
        try { return await operation.Value.ConfigureAwait(false); }
        finally { Pending.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(description, operation)); }
    }

    internal static bool IsShareableDescription(string value) =>
        Encoding.UTF8.GetByteCount(value) is > 0 and <= 500 &&
        Regex.IsMatch(value, @"^[A-Za-z0-9 .,:;/+()%#&'–-]+\z") &&
        Regex.IsMatch(value, @"[A-Za-z]{3}") &&
        !Regex.IsMatch(value, @"\b(?:[A-Z0-9]{17}|[0-9]{7,}|VIN|VCID|License Plate|Serial Number|Registration|Mileage|Freeze Frame)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static async Task<string> DownloadAsync(string description)
    {
        await Requests.WaitAsync().ConfigureAwait(false);
        try
        {
            string local = Local(description);
            if (local.Length > 0) return local;
            var now = DateTimeOffset.UtcNow;
            if (now < serviceRetryAfter || RetryAfter.TryGetValue(description, out var retry) && now < retry) return "";

            string query = "https://api.mymemory.translated.net/get?langpair=en%7Cpl&q=" + Uri.EscapeDataString(description);
            using var response = await Client.GetAsync(query).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                serviceRetryAfter = now.AddMinutes(15);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = result.RootElement;
            if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
                serviceRetryAfter = now.AddMinutes(15);
            if (now < serviceRetryAfter || !root.TryGetProperty("responseStatus", out var status) || status.ToString() != "200" ||
                !root.TryGetProperty("responseData", out var data) || !data.TryGetProperty("translatedText", out var translated))
                return "";
            string text = WebUtility.HtmlDecode(translated.GetString() ?? "").Trim();
            if (text.Length == 0 || text.Length > 1500 || text.Equals(description, StringComparison.OrdinalIgnoreCase) ||
                text.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                return "";

            lock (CacheLock)
            {
                Cache.Value[description] = text;
                try { Store.WriteAtomic(CachePath, JsonSerializer.Serialize(Cache.Value, Store.Options)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The translation can still be used in this session.
                }
            }
            RetryAfter.TryRemove(description, out _);
            return text;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var retry = DateTimeOffset.UtcNow.AddMinutes(2);
            if (error is HttpRequestException or TaskCanceledException)
                serviceRetryAfter = serviceRetryAfter > retry ? serviceRetryAfter : retry;
            else RetryAfter[description] = retry;
            return "";
        }
        finally { Requests.Release(); }
    }
}