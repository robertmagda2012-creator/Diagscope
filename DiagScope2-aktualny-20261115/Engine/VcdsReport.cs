using System.Globalization;
using System.Text.RegularExpressions;

namespace DiagScope2;

public sealed record ReportFault(string Controller, string Code, string Details);

public sealed record VcdsModule(string Address, string Name, string Labels)
{
    public string Software { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string Component { get; set; } = "";
    public string Coding { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Serial { get; set; } = "";
    public string SoftwareVersion { get; set; } = "";
    public string HardwareVersion { get; set; } = "";
    public string WorkshopCode { get; set; } = "";
    public string Vcid { get; set; } = "";
    public string AsamDataset { get; set; } = "";
    public string RodFile { get; set; } = "";
    public string Readiness { get; set; } = "";
    public List<string> SubsystemDetails { get; set; } = [];
    public int? ReportedFaults { get; set; }
}

public sealed record VehicleReport(Vehicle Vehicle, List<ReportFault> Faults)
{
    public DateTime? ScannedAt { get; init; }
    public int? MileageKm { get; init; }
    public string Chassis { get; init; } = "";
    public string ChassisDescription { get; init; } = "";
    public string Version { get; init; } = "";
    public string DataVersion { get; init; } = "";
    public string RepairOrder { get; init; } = "";
    public string LicensePlate { get; init; } = "";
    public string EndDetails { get; init; } = "";
    public int? StandaloneFaultCount { get; init; }
    public bool AmbiguousVin { get; init; }
    public List<string> RequestedAddresses { get; init; } = [];
    public List<VcdsModule> Modules { get; init; } = [];
    public bool Recognized => Modules.Count > 0 || RequestedAddresses.Count > 0 || Faults.Count > 0 ||
        Version.Length > 0 && (Vehicle.Vin.Length > 0 || AmbiguousVin);

    const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex ModuleHeader = new(@"^(?:Address|Adres|Adresse)\s+(?<address>[0-9A-F]{2})\s*[:–-]\s*(?<name>.*?)(?:\s+(?:Labels?|Labeldatei|Etykiety)\s*:\s*(?<labels>.*))?$", IgnoreCase);
    static readonly Regex SectionStart = new(@"(?m)^[\t ]*[\p{L}]+,\d{1,2},[\p{L}.]+,\d{4},\d{1,2}:\d{2}:\d{2}(?::\d+)?[\t ]*\r?$", IgnoreCase);

    public static List<VehicleReport> ReadAll(string text) => ReadAll(text, false);

    public static List<VehicleReport> ReadAll(string text, bool standaloneFaultReading)
    {
        var offsets = SectionStart.Matches(text).Select(match => match.Index).Prepend(0).Append(text.Length).Distinct().ToArray();
        var reports = new List<VehicleReport>();
        for (int i = 1; i < offsets.Length; i++)
        {
            var report = Read(text[offsets[i - 1]..offsets[i]], standaloneFaultReading);
            if (standaloneFaultReading && (report.Faults.Count > 0 || report.StandaloneFaultCount.HasValue ||
                report.Modules.Any(module => module.ReportedFaults.HasValue)))
                reports.Add(report);
            else if (!standaloneFaultReading && report.Recognized && (report.RequestedAddresses.Count > 0 || report.Faults.Count > 0 ||
                report.Modules.Any(module => module.ReportedFaults.HasValue || module.Software.Length > 0) || report.Vehicle.Vin.Length > 0 || report.AmbiguousVin))
                reports.Add(report);
        }
        return reports;
    }

    public static VehicleReport Read(string text) => Read(text, false);

    public static VehicleReport Read(string text, bool standaloneFaultReading)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).ToArray();
        var header = string.Join("\n", lines.TakeWhile(line => !ModuleHeader.IsMatch(line)));
        var vins = Regex.Matches(standaloneFaultReading ? string.Join("\n", lines) : header, @"(?im)^VIN(?:[\t ]+Number)?[\t ]*:[\t ]*([A-HJ-NPR-Z0-9]{17})(?![A-Z0-9])")
            .Select(m => LogMetadata.ValidVin(m.Groups[1].Value)).Distinct().ToArray();
        var chassis = Regex.Match(Field(header, "Chassis Type|Typ (?:nadwozia|podwozia)|Fahrzeugtyp"),
            @"^(?<code>[\w-]+)(?:\s+-\s+(?<description>.*)|\s+\(\s*(?:[\w-]+\s+-\s+)?(?<description>.*)\))?$");
        string description = chassis.Groups["description"].Value.Trim();
        var model = Regex.Match(description, @"^(?<make>VW|Volkswagen|Audi|Škoda|Skoda|Seat|Cupra|Bentley|Lamborghini)\s+(?<model>.+?)(?:\s*\([^()]*\))?$", IgnoreCase);
        string make = Field(header, "Marka|Make|Brand");
        string modelName = Field(header, "Model");
        string production = Field(header, "Data produkcji|Production date|Manufacture date|Produktionsdatum");
        var vehicle = new Vehicle
        {
            Vin = vins.Length == 1 ? vins[0] : "",
            Make = make.Length > 0 ? make : model.Groups["make"].Value,
            Model = modelName.Length > 0 ? modelName : model.Groups["model"].Value,
            Engine = Field(header, "Kod silnika|Engine code|Motorkennbuchstabe"),
            Capacity = Field(header, "Pojemność(?: silnika)?|(?:Engine )?Displacement|Hubraum"),
            ProductionDate = DateTime.TryParseExact(production, ["yyyy-MM-dd", "d.M.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var productionDate) ? productionDate : null,
            ProductionYear = Vehicle.ParseProductionYear(production) ?? Vehicle.ParseProductionYear(Field(header, "Rok produkcji|Production year|Manufacture year|Baujahr")),
            EngineType = Field(header, "Typ silnika|Engine type|Motortyp"),
            Fuel = Field(header, "Rodzaj paliwa|Fuel type|Kraftstoffart"),
            GearboxCode = Field(header, "Kod skrzyni(?: biegów)?|(?:Gearbox|Transmission) code|Getriebekennbuchstabe"),
            PaintCode = Field(header, "Kod lakieru|Paint code|Lackcode"),
            Equipment = Field(header, "Wyposażenie|Equipment|Ausstattung")
        };
        var miles = Regex.Match(header, @"\b(?:Mileage|Przebieg|Kilometerstand)[\t ]*:[\t ]*(\d+)[\t ]*km\b", IgnoreCase);
        int? mileage = int.TryParse(miles.Groups[1].Value, CultureInfo.InvariantCulture, out int km) ? km : null;
        string scanLines = Regex.Match(header, @"(?im)^(?:Scan|Skan|Scanliste)[\t ]*:[\t ]*(?<addresses>[^\n]*(?:\n(?:[0-9A-F]{2}[\t ]+)*[0-9A-F]{2}[\t ]*(?=\n|$))*)").Groups["addresses"].Value;
        var addresses = Regex.Matches(scanLines, @"\b[0-9A-F]{2}\b", IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
        int? standaloneFaultCount = null;
        var faults = new List<ReportFault>();
        var modules = new List<VcdsModule>();
        VcdsModule? module = null;
        string code = "", endDetails = "";
        var details = new List<string>();
        bool inFaults = false, inSubsystem = false;

        void Flush()
        {
            if (code.Length > 0)
                faults.Add(new(ControllerLabel(module), code, string.Join("\n", details).Trim()));
            code = "";
            details.Clear();
        }

        foreach (string line in lines)
        {
            var section = ModuleHeader.Match(line);
            if (section.Success)
            {
                Flush();
                module = new(section.Groups["address"].Value.ToUpperInvariant(), section.Groups["name"].Value.Trim(), section.Groups["labels"].Value.Trim());
                modules.Add(module);
                inFaults = inSubsystem = false;
                continue;
            }
            if (Regex.IsMatch(line, @"^(?:End|Koniec|Ende)\b", IgnoreCase))
            {
                Flush();
                endDetails = line;
                break;
            }
            if (Regex.IsMatch(line, @"^(?:No faults?(?: codes?)? found|Brak (?:kodów błędów|błędów|usterek)|Nie znaleziono (?:błędów|usterek)|Kein(?:e)? Fehler(?:code)? gefunden)\b", IgnoreCase))
            {
                Flush();
                if (module is not null) module.ReportedFaults = 0;
                else if (standaloneFaultReading) standaloneFaultCount = 0;
                inFaults = inSubsystem = false;
                continue;
            }
            var count = Regex.Match(line, @"^(?:Znaleziono\s+)?(?<count>\d+)\s+(?:Faults? Found|błęd\p{L}*|uster\p{L}*|Fehler(?: gefunden)?)\b", IgnoreCase);
            if (count.Success && int.TryParse(count.Groups["count"].Value, out int found))
            {
                Flush();
                if (module is not null) module.ReportedFaults = found;
                else if (standaloneFaultReading) standaloneFaultCount = found;
                inFaults = found > 0;
                inSubsystem = false;
                continue;
            }
            var readiness = Regex.Match(line, @"^(?:Readiness|Gotowość)\s*:\s*(.*)$", IgnoreCase);
            if (module is not null && readiness.Success)
            {
                Flush();
                inFaults = inSubsystem = false;
                module.Readiness = readiness.Groups[1].Value;
                continue;
            }
            if (standaloneFaultReading && module is null && !inFaults &&
                Regex.IsMatch(line, @"^(?:Control Module Part Number|Component and/or Version|Part No(?: SW)?|Nr części(?: SW)?)\s*:", IgnoreCase))
            {
                module = new("", "", "");
                modules.Add(module);
            }
            if (module is not null && !inFaults)
            {
                if (Regex.IsMatch(line, @"^[-=]{3,}$")) inSubsystem = false;
                if (Regex.IsMatch(line, @"^(?:Subsystem|Podsystem|Subsysteme?)\s+\d+\s*[-:]", IgnoreCase)) inSubsystem = true;
                if (inSubsystem && line.Length > 0)
                {
                    module.SubsystemDetails.Add(line);
                    continue;
                }
                var part = Regex.Match(line, @"^(?:Part No(?: SW)?|Control Module Part Number|Teilenummer(?: SW)?|Nr części(?: SW)?)\s*:\s*(?<sw>.*?)(?:\s+HW\s*:\s*(?<hw>.+))?$", IgnoreCase);
                if (part.Success)
                {
                    module.Software = part.Groups["sw"].Value.Trim();
                    module.Hardware = part.Groups["hw"].Value.Trim();
                }
                var field = Regex.Match(line, @"^(?<key>Component(?: and/or Version)?|Bauteil|Komponent|(?:Software )?Coding|Codierung|Kodowanie|Revision|Rewizja|Serial number|Software Version|Hardware Version|Shop #|Work Shop Code|VCID|ASAM Dataset|ROD)\s*:\s*(?<value>.*)", IgnoreCase);
                if (field.Success)
                {
                    string value = field.Groups["value"].Value;
                    switch (field.Groups["key"].Value.ToLowerInvariant())
                    {
                        case "component": case "component and/or version": case "bauteil": case "komponent": module.Component = value; break;
                        case "coding": case "software coding": case "codierung": case "kodowanie": module.Coding = value; break;
                        case "serial number": module.Serial = value; break;
                        case "software version": module.SoftwareVersion = value; break;
                        case "hardware version": module.HardwareVersion = value; break;
                        case "shop #": case "work shop code": module.WorkshopCode = value; break;
                        case "vcid": module.Vcid = value; break;
                        case "asam dataset": module.AsamDataset = value; break;
                        case "rod": module.RodFile = value; break;
                        default:
                            var serial = Regex.Match(value, @"\s+Serial number\s*:\s*(.*)$", IgnoreCase);
                            module.Revision = serial.Success ? value[..serial.Index].Trim() : value;
                            if (serial.Success) module.Serial = serial.Groups[1].Value.Trim();
                            break;
                    }
                }
            }
            var fault = Regex.Match(line, @"^(?<code>\d{4,7}|[PCBU][0-9A-F]{4})\s*(?:[-:]\s*|(?=\d\d\s*\[))(?<details>.+)$", IgnoreCase);
            if (fault.Success && (char.IsLetter(fault.Groups["code"].Value[0]) ||
                (inFaults && (standaloneFaultReading || fault.Groups["code"].Length is 5 or 6)) ||
                standaloneFaultReading && fault.Groups["code"].Length == 5))
            {
                inFaults = true;
                string nextCode = fault.Groups["code"].Value.ToUpperInvariant();
                if (Regex.IsMatch(code, @"^\d+$") && details.Count <= 1 && char.IsLetter(nextCode[0]) &&
                    (!standaloneFaultReading || Regex.IsMatch(fault.Groups["details"].Value, @"^\d{2}(?:[-\s]|$)")))
                {
                    code += " / " + nextCode;
                    details.Add(line);
                    continue;
                }
                Flush();
                code = nextCode;
                details.Add(fault.Groups["details"].Value);
            }
            else if (code.Length > 0 && !Regex.IsMatch(line, @"^[-=]{3,}$"))
                details.Add(line);
        }
        Flush();
        if (vehicle.Capacity.Length == 0)
        {
            string component = modules.FirstOrDefault(item => item.Address == "01")?.Component ?? "";
            var litres = Regex.Match(component, @"^(?<value>[0-9]+(?:[.,][0-9]+)?)\s*L\b", IgnoreCase);
            if (litres.Success) vehicle.Capacity = litres.Groups["value"].Value.Replace('.', ',') + " l";
        }

        return new(vehicle, faults)
        {
            ScannedAt = ReadDate(lines),
            MileageKm = mileage,
            Chassis = chassis.Groups["code"].Value,
            ChassisDescription = description,
            Version = Field(header, "VCDS[ -]Version|VCDS Version"),
            DataVersion = Field(header, "Data version|Wersja danych|Datenstand"),
            RepairOrder = Regex.Match(header, @"\b(?:Repair Order|Numer zlecenia|Auftragsnummer)[\t ]*:[\t ]*(?<order>[^\n]*)", IgnoreCase).Groups["order"].Value.Trim(),
            LicensePlate = Regex.Match(header, @"\b(?:License Plate|Numer rejestracyjny|Kennzeichen)[\t ]*:[\t ]*(?<plate>[^\r\n]*?)(?=[\t ]*(?:$|\r|\n|(?:Mileage|Repair Order)[\t ]*:))", IgnoreCase | RegexOptions.Multiline).Groups["plate"].Value.Trim(),
            EndDetails = endDetails,
            StandaloneFaultCount = standaloneFaultCount,
            AmbiguousVin = vins.Length > 1,
            RequestedAddresses = addresses,
            Modules = modules
        };
    }

    public static string ControllerLabel(VcdsModule? module)
    {
        if (module is null) return "";
        string identity = (module.Address + " " + module.Name).Trim();
        if (identity.Length > 0) return identity;
        return string.Join(" · ", new[] { module.Software, module.Component }.Where(value => value.Length > 0));
    }

    static DateTime? ReadDate(IEnumerable<string> lines)
    {
        foreach (string line in lines.Take(5))
        {
            var date = Regex.Match(line, @"^[\p{L}]+,(\d{1,2}),([\p{L}.]+),(\d{4}),(\d{1,2}:\d{2}:\d{2})");
            if (!date.Success) continue;
            string value = string.Join(" ", date.Groups.Cast<Group>().Skip(1).Select(g => g.Value));
            foreach (string culture in new[] { "en-US", "pl-PL", "de-DE" })
                if (DateTime.TryParseExact(value, ["d MMMM yyyy H:mm:ss", "d MMM yyyy H:mm:ss"],
                    CultureInfo.GetCultureInfo(culture), DateTimeStyles.AllowWhiteSpaces, out var result))
                    return result;
        }
        return null;
    }

    static string Field(string text, string name) =>
        Regex.Match(text, @"(?im)^[\t ]*(?:" + name + @")[\t ]*:[\t ]*(?<value>[^\r\n]+)").Groups["value"].Value.Trim();
}

public sealed record SavedAutoScan(string Name, string Hash, VehicleReport Report)
{
    public string ImportSource { get; init; } = "";
    public int Section { get; init; }
    public DateTimeOffset? ImportedAt { get; init; }
}

public sealed record ModuleReading(string Source, string Key, DateTime? RecordedAt, VcdsModule Module);