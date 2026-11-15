using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiagScope2;

public static class LogMetadata
{
    public static DateTime? LocalTime(LogData log) => log.RecordedAt?.LocalDateTime ?? log.RecordedLocal;

    public static string FileStem(LogData log)
    {
        string time = LocalTime(log)?.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) ?? "bez-daty-pomiaru";
        string file = Path.GetFileNameWithoutExtension(log.Name);
        string label = Regex.Replace(file, @"[^\p{L}\p{Nd}._-]", "_").Trim('.');
        return time + "_" + (label.Length == 0 ? "log" : label[..Math.Min(60, label.Length)]);
    }

    public static string ValidVin(string value)
    {
        string vin = value.Trim().ToUpperInvariant();
        return Regex.IsMatch(vin, "^[A-HJ-NPR-Z0-9]{17}$") ? vin : "";
    }

    public static void ReadVcdsHeader(LogData log, List<string[]> rows)
    {
        var header = rows.TakeWhile(row => !row.Any(cell => cell.Trim().Equals("STAMP", StringComparison.OrdinalIgnoreCase))).Take(20);
        foreach (var row in header)
        {
            var line = string.Join(",", row);
            var vin = Regex.Match(line, @"\bVIN\s*[:=,]?\s*([A-HJ-NPR-Z0-9]{17})\b", RegexOptions.IgnoreCase);
            if (vin.Success && log.Vin.Length == 0) log.Vin = ValidVin(vin.Groups[1].Value);
            var ecu = Regex.Match(line, @"(?<![A-Z0-9])([A-Z0-9]{3})[- ]([0-9]{3})[- ]([0-9]{3})(?:[- ]([A-Z]{1,3}))?(?![A-Z0-9])");
            if (ecu.Success && log.Ecu.Length == 0)
                log.Ecu = string.Join("-", ecu.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value));
            if (log.RecordedLocal.HasValue || row.Length < 5) continue;
            var time = Regex.Match(row[4].Trim(), @"^\d{1,2}:\d{2}:\d{2}");
            if (!time.Success) continue;
            string value = $"{row[1].Trim()} {row[2].Trim()} {row[3].Trim()} {time.Value}";
            foreach (string culture in new[] { "en-US", "pl-PL", "de-DE", "pt-PT" })
            {
                if (!DateTime.TryParseExact(value, ["d MMMM yyyy H:mm:ss", "d MMM yyyy H:mm:ss"],
                    CultureInfo.GetCultureInfo(culture), DateTimeStyles.AllowWhiteSpaces, out var date)) continue;
                log.RecordedLocal = date;
                break;
            }
        }
    }

    public static string KeyFor(string ecu, string channel)
    {
        string controller = Regex.Replace(ecu.ToUpperInvariant(), "[^A-Z0-9]", "");
        return controller.Length == 0 || channel.Length == 0 ? "" : controller + "|" + channel;
    }

    public static string ChannelLabel(Channel channel) =>
        channel.Unit.Length > 0 ? $"{channel.Name} [{channel.Unit}]" : channel.Name;

    // Verified against 078-907-552-ALF.lbl and the units in the 4B0-907-552-F CSV.
    static readonly Dictionary<(string Address, string Unit), string> AlfLabels = new()
    {
        [("031.1", "V")] = "Napięcie sondy B1S1 (rzeczywiste)",
        [("031.3", "V")] = "Napięcie sondy B2S1 (rzeczywiste)",
        [("033.1", "%")] = "Regulacja lambda B1S1",
        [("033.2", "V")] = "Napięcie sondy B1S1",
        [("033.3", "%")] = "Regulacja lambda B2S1",
        [("033.4", "V")] = "Napięcie sondy B2S1"
    };

    static string LabelsPath => Path.Combine(Store.Root, "channel-labels.json");

    static Dictionary<string, string> ReadNames()
    {
        if (!File.Exists(LabelsPath)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LabelsPath)) ?? new();
    }

    public static void ApplyNames(LogData log)
    {
        ApplyVerifiedLabels(log);
        Dictionary<string, string>? names = null;
        foreach (var channel in log.Channels)
        {
            if (channel.Key.StartsWith("VCDS:", StringComparison.Ordinal))
                channel.Key = KeyFor(log.Ecu, channel.Key);
            if (channel.Key.Length > 0 && (names ??= ReadNames()).TryGetValue(channel.Key, out string? name))
                channel.Name = name;
        }
    }

    public static void ApplyVerifiedLabels(LogData log)
    {
        if (log.Source != "VCDS CSV" || KeyFor(log.Ecu, "VCDS") != "4B0907552F|VCDS") return;
        foreach (var channel in log.Channels)
        {
            if ((channel.Name == channel.OriginalName || channel.Name is "" or "Kanał bez opisu") &&
                (channel.OriginalName is "" or "Lambda" or "Voltage") &&
                AlfLabels.TryGetValue((channel.Address, channel.Unit), out string? verifiedName))
                channel.Name = verifiedName;
        }
    }

    public static void SaveName(Channel channel, string name)
    {
        if (channel.Key.Length == 0) return;
        var names = ReadNames();
        names[channel.Key] = name;
        Store.WriteAtomic(LabelsPath, JsonSerializer.Serialize(names, Store.Options));
    }
}