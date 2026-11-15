using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace DiagScope2;

public partial class MainWindow
{
    void PickAutoScan()
    {
        var picker = new OpenFileDialog
        {
            Filter = "Raport VCDS (*.txt;*.log)|*.txt;*.log",
            InitialDirectory = VcdsLogsDirectory()
        };
        if (picker.ShowDialog() == true) ImportReport(picker.FileName);
    }

    void ImportReport(string path) => ImportReportForVehicle(path, null);

    void ImportReportForVehicle(string path, string? selectedVin)
    {
        byte[] bytes = Importer.ReadBytes(path);
        var reports = VehicleReport.ReadAll(Importer.Decode(bytes));
        if (reports.Count == 0)
            throw new FormatException("Nie znaleziono odczytu diagnostycznego w pliku. Wybierz Auto-Scan albo zapis błędów sterownika z VCDS.");

        var profiles = VehicleProfiles.Read();
        var assignments = new List<Vehicle>();
        foreach (var report in reports)
        {
            string vin = report.Vehicle.Vin;
            var matches = vin.Length == 0 ? [] : profiles.Where(profile => LogMetadata.ValidVin(profile.Vehicle.Vin) == vin).ToArray();
            if (vin.Length > 0 && matches.Length < 2)
            {
                assignments.Add(matches.SingleOrDefault()?.Vehicle ?? new Vehicle { Vin = vin });
                continue;
            }
            Vehicle? selected = selectedVin is not null
                ? profiles.SingleOrDefault(profile => LogMetadata.ValidVin(profile.Vehicle.Vin) == selectedVin)?.Vehicle
                : SelectVehicleForScan(matches.Length > 1 ? matches : profiles, report.AmbiguousVin || matches.Length > 1);
            if (selected is null) return;
            assignments.Add(selected);
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var now = DateTimeOffset.Now;
        for (int i = 0; i < reports.Count; i++)
        {
            var assignment = assignments[i];
            if (assignment.ProfileId.Length > 0)
                assignment = VehicleProfiles.Read().Single(profile => profile.Vehicle.ProfileId == assignment.ProfileId).Vehicle;
            else if (assignment.Vin.Length > 0)
                assignment = VehicleProfiles.Read().SingleOrDefault(profile => LogMetadata.ValidVin(profile.Vehicle.Vin) == assignment.Vin)?.Vehicle ?? assignment;
            SaveAutoScan(new(Path.GetFileName(path), hash, reports[i]) { Section = i, ImportedAt = now }, bytes, assignment);
        }
        ShowVehicleCard();
        StorageState.Text = "Zapisano odczyt VCDS";
    }

    Vehicle? SelectVehicleForScan(IReadOnlyList<VehicleProfile> profiles, bool ambiguousVin)
    {
        var dialog = new FormDialog(this, "Do którego auta dodać odczyt?");
        dialog.Height = 340;
        dialog.MinHeight = 300;
        dialog.Body.Children.Add(Txt(ambiguousVin
            ? "VIN nie wskazuje jednej karty. Wybierz pojazd."
            : "Odczyt nie zawiera VIN-u. Wybierz pojazd.", 14));
        var choices = profiles.Select(profile => (profile.Label, Vehicle: profile.Vehicle))
            .Append(("Zapisz bez przypisania", new Vehicle())).ToArray();
        var pick = new ComboBox { ItemsSource = choices.Select(choice => choice.Item1), SelectedIndex = -1 };
        dialog.Body.Children.Add(pick);
        dialog.Accept = () =>
        {
            if (pick.SelectedIndex < 0) throw new InvalidOperationException("Wybierz kartę pojazdu albo zapis bez przypisania.");
        };
        return dialog.ShowDialog() == true ? choices[pick.SelectedIndex].Item2 : null;
    }

    void SaveAutoScan(SavedAutoScan scan, byte[] bytes, Vehicle assignment)
    {
        var report = scan.Report;
        string vin = LogMetadata.ValidVin(assignment.Vin);
        bool sameVehicle = (assignment.ProfileId.Length > 0 && study.Vehicle.ProfileId == assignment.ProfileId) ||
            (study.Vehicle.ProfileId.Length == 0 && vin.Length > 0 && LogMetadata.ValidVin(study.Vehicle.Vin) == vin);
        bool sameUnassignedReport = assignment.ProfileId.Length == 0 && vin.Length == 0 &&
            study.Vehicle.ProfileId.Length == 0 && study.Vehicle.Vin.Length == 0 && study.AutoScans.Any(saved => saved.Hash == scan.Hash);
        bool emptyStudy = study.Vehicle.ProfileId.Length == 0 && study.Vehicle.Vin.Length == 0 &&
            study.Vehicle.Make.Length == 0 && study.Vehicle.Model.Length == 0 && study.Vehicle.Engine.Length == 0 &&
            study.Vehicle.Capacity.Length == 0 && study.Vehicle.HasLpg is null && !HasStudyData(study);
        bool switchStudy = !sameVehicle && !emptyStudy && !sameUnassignedReport;
        if (switchStudy && !Persist()) throw new IOException("Zapis bieżącego badania nie powiódł się.");
        var target = switchStudy ? new Study() : study;
        int existingIndex = target.AutoScans.FindIndex(saved => saved.ImportSource.Length == 0 && saved.Hash == scan.Hash && saved.Section == scan.Section);
        var existingScan = existingIndex < 0 ? null : target.AutoScans[existingIndex];
        if (existingScan is not null)
            scan = scan with { Name = existingScan.Name, ImportedAt = existingScan.ImportedAt };

        var oldVehicle = target.Vehicle;
        var vehicle = (assignment.ProfileId.Length > 0 ? assignment : oldVehicle).Copy();
        vehicle.Vin = vin;
        if (vehicle.Make.Length == 0) vehicle.Make = report.Vehicle.Make;
        if (vehicle.Model.Length == 0) vehicle.Model = report.Vehicle.Model;
        if (vehicle.Engine.Length == 0) vehicle.Engine = report.Vehicle.Engine;
        if (vehicle.EngineType.Length == 0) vehicle.EngineType = report.Vehicle.EngineType;
        if (vehicle.Capacity.Length == 0) vehicle.Capacity = report.Vehicle.Capacity;
        if (!vehicle.ProductionDate.HasValue &&
            (!vehicle.ProductionYear.HasValue || vehicle.ProductionYear == report.Vehicle.ProductionDate?.Year))
            vehicle.ProductionDate = report.Vehicle.ProductionDate;
        if (!vehicle.ProductionDate.HasValue) vehicle.ProductionYear ??= report.Vehicle.ProductionYear;
        if (vehicle.Equipment.Length == 0) vehicle.Equipment = report.Vehicle.Equipment;
        if (vehicle.Fuel.Length == 0) vehicle.Fuel = report.Vehicle.Fuel;
        if (vehicle.GearboxCode.Length == 0) vehicle.GearboxCode = report.Vehicle.GearboxCode;
        if (vehicle.PaintCode.Length == 0) vehicle.PaintCode = report.Vehicle.PaintCode;
        if (vehicle.Chassis.Length == 0) vehicle.Chassis = report.Chassis;
        if (vehicle.Registration.Length == 0) vehicle.Registration = report.LicensePlate;
        if (vehicle.Source.Length == 0) vehicle.Source = scan.Name;
        DateTime? legacyDate = vehicle.LastScanAt;
        if (vehicle.LastScanMileageKm.HasValue) vehicle.LastMileageAt ??= legacyDate;
        if (report.MileageKm.HasValue && (!vehicle.LastMileageAt.HasValue || report.ScannedAt >= vehicle.LastMileageAt))
        {
            vehicle.LastScanMileageKm = report.MileageKm;
            vehicle.LastMileageAt = report.ScannedAt;
        }
        if (!vehicle.LastScanAt.HasValue || report.ScannedAt >= vehicle.LastScanAt)
            vehicle.LastScanAt = report.ScannedAt;
        RecordModuleReadings(vehicle, legacyDate, scan);

        string archive = ScanArchivePath(target, scan);
        if (!File.Exists(archive)) Store.WriteAtomic(archive, bytes);
        if (vin.Length > 0 || assignment.ProfileId.Length > 0) VehicleProfiles.Save(vehicle);

        var addedFaults = existingScan is not null ? new List<Entry>() : report.Faults.Select(fault => new Entry
        {
            Title = FaultTitle(fault),
            Source = scan.Name,
            Status = Statuses[2],
            Note = fault.Details
        }).ToList();
        target.Vehicle = vehicle;
        if (existingScan is null) target.AutoScans.Add(scan);
        else target.AutoScans[existingIndex] = scan;
        target.Dtcs.AddRange(addedFaults);
        try { Store.Save(target); }
        catch
        {
            target.Vehicle = oldVehicle;
            if (existingScan is null) target.AutoScans.Remove(scan);
            else target.AutoScans[existingIndex] = existingScan;
            foreach (var fault in addedFaults) target.Dtcs.Remove(fault);
            throw;
        }

        if (switchStudy) SetActiveStudy(target);
        studyIsDraft = false;
    }

    static void RecordModuleReadings(Vehicle vehicle, DateTime? legacyDate, SavedAutoScan scan)
    {
        var readings = vehicle.ModuleReadings.ToList();
        if (readings.Count == 0)
            readings.AddRange(vehicle.ScanModules.Select(module => new ModuleReading(vehicle.Source, "", legacyDate, module)));
        string key = scan.Hash + "/" + scan.Section;
        var refreshed = scan.Report.Modules.Select(module => new ModuleReading(scan.Name, key, scan.Report.ScannedAt, module)).ToList();
        int index = readings.FindIndex(reading => reading.Key == key);
        if (index < 0) readings.AddRange(refreshed);
        else
        {
            readings.RemoveAll(reading => reading.Key == key && refreshed.Any(item => item.Module.Address == reading.Module.Address));
            readings.InsertRange(Math.Min(index, readings.Count), refreshed);
        }
        vehicle.ModuleReadings = readings;
        vehicle.ScanModules = readings.GroupBy(reading => reading.Module.Address)
            .Select(group => group.Reverse().OrderByDescending(reading => reading.RecordedAt).First().Module)
            .OrderBy(module => module.Address, StringComparer.Ordinal).ToList();
    }

    static string ScanArchivePath(Study owner, SavedAutoScan scan)
    {
        if (!Regex.IsMatch(scan.Hash, "^[0-9A-F]{64}$"))
            throw new InvalidDataException("Nieprawidłowy identyfikator skanu.");
        string vin = LogMetadata.ValidVin(scan.Report.Vehicle.Vin);
        return Path.Combine(Store.Root, "Vehicles", vin.Length > 0 ? vin : owner.Id, "AutoScan", scan.Hash + ".txt");
    }

    void AddVehicleScans(Study owner, Vehicle vehicle, bool latest = true)
    {
        var scans = owner.AutoScans.AsEnumerable().Reverse().OrderByDescending(scan => scan.ImportSource.Length > 0 ? scan.ImportedAt?.LocalDateTime : scan.Report.ScannedAt).ToArray();
        var readings = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (scans.Length > 0)
        {
            readings.Children.Add(ShowAutoScan(scans[0], owner, vehicle));
            if (scans.Length > 1)
            {
                var previous = new StackPanel();
                foreach (var scan in scans.Skip(1))
                    previous.Children.Add(new Expander { Header = ReadingLabel(scan), Content = ShowAutoScan(scan, owner, vehicle) });
                readings.Children.Add(new Expander { Header = "Wcześniejsze odczyty w tym badaniu", Content = previous });
            }
        }

        var scanEntries = scans.SelectMany(scan => scan.Report.Faults.Select(fault => (scan.Name, Title: FaultTitle(fault)))).ToHashSet();
        var legacy = owner.Dtcs.Where(entry => !scanEntries.Contains((entry.Source, entry.Title))).ToArray();
        if (legacy.Length > 0)
        {
            var legacyList = new StackPanel();
            foreach (var entry in legacy)
            {
                var parts = entry.Title.Split(" · ", 2, StringSplitOptions.TrimEntries);
                var fault = new ReportFault(parts.ElementAtOrDefault(1) ?? "", parts[0], entry.Note);
                legacyList.Children.Add(Card(Stack(FaultRow(fault, ""),
                    new Expander { Header = "Szczegóły wpisu", Content = Txt(entry.Note, 13) },
                    Btn("Zapisz następny test", () => SaveDtcTest(owner, vehicle, entry)))));
            }
            readings.Children.Add(new Expander { Header = "Wcześniej zapisane wpisy DTC", Content = legacyList });
        }
        if (readings.Children.Count == 0)
            readings.Children.Add(Txt("W tym badaniu nie zapisano odczytu błędów DTC.", 14));
        var section = new Expander
        {
            Header = (latest ? "Błędy DTC z ostatniego badania" : "Błędy DTC z otwartego badania") + $" · {owner.Date:dd.MM.yyyy HH:mm}",
            Content = readings,
            IsExpanded = true
        };
        if (owner.Id == study.Id || vehicleScans is null) vehicleScans = section;
        Page.Children.Add(Card(section));
    }

    Border ShowAutoScan(SavedAutoScan scan, Study owner, Vehicle vehicle)
    {
        var report = scan.Report;
        string name = string.Join(" ", new[] { vehicle.Make, vehicle.Model }.Where(value => value.Length > 0));
        string mileage = report.MileageKm is int km ? $"{km:N0} km" : "";
        var identity = Txt(string.Join(" · ", new[] { name, ReadingLabel(scan), mileage }.Where(value => value.Length > 0)), 16);
        identity.FontWeight = FontWeights.SemiBold;
        var panel = Stack(identity);
        var metadata = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (scan.ImportSource.Length == 0) AddScanField(metadata, "Plik", scan.Name);
        else AddScanField(metadata, "Data odczytu z VCDS", ScanDate(report.ScannedAt));
        AddScanField(metadata, "Wersja VCDS", report.Version);
        AddScanField(metadata, "Wersja danych VCDS", report.DataVersion);
        AddScanField(metadata, "Numer zlecenia", report.RepairOrder);
        AddScanField(metadata, "Numer rejestracyjny z raportu", report.LicensePlate);
        AddScanField(metadata, "Typ podwozia z raportu", report.ChassisDescription);
        AddScanField(metadata, "Podsumowanie skanu", report.EndDetails);
        panel.Children.Add(new Expander { Header = "Informacje o raporcie", Content = metadata, Margin = new Thickness(0, 0, 0, 10) });

        int clean = report.Modules.Count(module => module.ReportedFaults == 0);
        var missing = report.RequestedAddresses.Except(report.Modules.Select(module => module.Address)).ToArray();
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(ScanStat(report.Faults.Count, "Kody błędów", report.Faults.Count > 0 ? "#922D25" : "#1457A6", report.Faults.Count > 0 ? "#FBEDEB" : "#E8F2FD"));
        stats.Children.Add(ScanStat(clean, "Sterowniki bez błędów", "#146344", "#E6F4EB"));
        stats.Children.Add(ScanStat(missing.Length, "Adresy bez wyniku", "#734806", "#FFF2D9"));
        panel.Children.Add(stats);

        panel.Children.Add(Txt("Lista błędów", 20));
        if (report.Faults.Count == 0)
        {
            string message = report.StandaloneFaultCount > 0 || report.Modules.Any(module => module.ReportedFaults > 0)
                ? "Raport zgłasza błędy. Nie udało się odczytać kodów z jego treści."
                : report.StandaloneFaultCount == 0 || clean > 0 ? "VCDS zgłosił brak błędów." : "Raport nie zawiera rozpoznanego wyniku odczytu błędów.";
            panel.Children.Add(Txt(message, 14));
        }
        else
        {
            var columns = FaultColumns("Kod", "Opis", "Sterownik", "Stan", "");
            columns.Background = B("#EAF2FA");
            panel.Children.Add(columns);
            foreach (var fault in report.Faults)
            {
                var row = FaultRow(fault, "⌄");
                var details = new StackPanel { Margin = new Thickness(12, 5, 4, 7) };
                details.Children.Add(Txt("Wpis z raportu VCDS", 13));
                details.Children.Add(Txt(fault.Details, 13));
                string title = FaultTitle(fault);
                var matchingEntries = owner.Dtcs.Where(item => item.Source == scan.Name && item.Title == title &&
                    (scan.ImportSource.Length == 0 || item.Date == scan.ImportedAt)).ToArray();
                var entry = matchingEntries.FirstOrDefault(item => item.Note == fault.Details) ?? matchingEntries.FirstOrDefault();
                if (entry is not null) details.Children.Add(Btn("Zapisz następny test", () => SaveDtcTest(owner, vehicle, entry)));
                details.Visibility = Visibility.Collapsed;
                var toggle = new ToggleButton { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                toggle.SetResourceReference(FrameworkElement.StyleProperty, "FaultRow");
                System.Windows.Automation.AutomationProperties.SetName(toggle, $"{fault.Code} · szczegóły błędu");
                toggle.Checked += (_, _) => details.Visibility = Visibility.Visible;
                toggle.Unchecked += (_, _) => details.Visibility = Visibility.Collapsed;
                panel.Children.Add(toggle);
                panel.Children.Add(details);
            }
        }

        var moduleStatus = new StackPanel { Margin = new Thickness(12, 8, 4, 0) };
        foreach (var module in report.Modules)
        {
            string status = module.ReportedFaults switch { 0 => "brak błędów", int count => $"zgłoszone błędy: {count}", _ => "brak statusu w raporcie" };
            string controller = FaultController(new ReportFault(VehicleReport.ControllerLabel(module), "", ""));
            var moduleDetails = Stack(Txt(controller + " · " + status, 14));
            if (scan.ImportSource.Length > 0)
            {
                AddScanField(moduleDetails, "Numer części / SW", module.Software);
                AddScanField(moduleDetails, "Numer części / HW", module.Hardware);
                AddScanField(moduleDetails, "Komponent", module.Component);
                AddScanField(moduleDetails, "Kodowanie", module.Coding);
            }
            moduleStatus.Children.Add(moduleDetails);
        }
        if (missing.Length > 0) moduleStatus.Children.Add(Txt("Bez wyniku w raporcie · " + string.Join(", ", missing), 14));
        if (moduleStatus.Children.Count > 0) panel.Children.Add(new Expander { Header = "Sterowniki w tym odczycie", Content = moduleStatus });
        panel.Children.Add(Btn("Otwórz oryginalny TXT", () => Process.Start(new ProcessStartInfo(ScanArchivePath(owner, scan)) { UseShellExecute = true })));
        return Card(panel);
    }

    void SaveDtcTest(Study owner, Vehicle vehicle, Entry entry)
    {
        if (study.Id != owner.Id) OpenVehicleStudy(owner.Id, vehicle);
        if (study.Id == owner.Id) AddDtcTest(entry);
    }

    static string FaultTitle(ReportFault fault) =>
        fault.Controller.Length == 0 ? fault.Code : fault.Code + " · " + fault.Controller;

    static string FaultController(ReportFault fault)
    {
        string address = fault.Controller.Split(' ', 2)[0];
        if (!Regex.IsMatch(address, "^[0-9A-F]{2}$"))
            return fault.Controller.Length > 0 ? fault.Controller : "Nie podano";
        string polish = PolishModule(address);
        return polish == "Sterownik" && fault.Controller.Length > address.Length
            ? fault.Controller : address + " " + polish;
    }

    static string ScanDate(DateTime? date) => date?.ToString("dd.MM.yyyy HH:mm:ss") ?? "Brak daty odczytu";

    static Border ScanStat(int count, string label, string text, string fill)
    {
        var number = Txt(count.ToString(), 20);
        number.FontWeight = FontWeights.SemiBold;
        number.Margin = new Thickness(0, 0, 0, 2);
        number.Foreground = B(text);
        var caption = Txt(label, 12);
        caption.Margin = new Thickness(0);
        return new Border { Background = B(fill), CornerRadius = new CornerRadius(7), Padding = new Thickness(9), Margin = new Thickness(0, 0, 5, 0), Child = Stack(number, caption) };
    }

    static Grid FaultColumns(params string[] values)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        double[] weights = [1.2, 2.75, 1.35, 1.15, .28];
        for (int i = 0; i < values.Length; i++)
        {
            row.ColumnDefinitions.Add(new() { Width = new GridLength(weights[i], GridUnitType.Star) });
            var cell = Txt(values[i], 13);
            cell.Margin = new Thickness(6, 7, 4, 7);
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        return row;
    }

    static string PolishModule(string address) => address switch
    {
        "01" => "Silnik", "02" => "Skrzynia biegów", "03" => "ABS", "06" => "Fotel pasażera",
        "08" => "Klimatyzacja", "09" => "Elektronika centralna", "15" => "Poduszki powietrzne",
        "16" => "Elektronika kierownicy", "17" => "Licznik", "18" => "Ogrzewanie dodatkowe",
        "19" => "Bramka CAN", "22" => "Napęd na wszystkie koła", "25" => "Immobilizer",
        "34" => "Regulacja poziomu", "35" => "Centralny zamek", "36" => "Fotel kierowcy",
        "37" => "Nawigacja", "44" => "Wspomaganie kierownicy", "46" => "Moduł komfortu",
        "55" => "Poziomowanie reflektorów", "56" => "Radio", "76" => "Asystent parkowania",
        _ => "Sterownik"
    };

    static string KnownPolishFault(ReportFault fault)
    {
        foreach (var code in fault.Code.Split(" / "))
        {
            string translated = code switch
            {
                "P0130" or "16514" => "Usterka obwodu sondy lambda B1S1",
                "P0150" or "16534" => "Usterka obwodu sondy lambda B2S1",
                "P1127" or "17535" => "Korekta mnożnikowa banku 1 · mieszanka zbyt bogata",
                "P1129" or "17537" => "Korekta mnożnikowa banku 2 · mieszanka zbyt bogata",
                "P1137" or "17545" => "Korekta addytywna banku 1 · mieszanka zbyt bogata",
                "P1138" or "17546" => "Korekta addytywna banku 2 · mieszanka zbyt uboga",
                "00532" when fault.Details.Contains("Signal too Low", StringComparison.OrdinalIgnoreCase) => "Napięcie zasilania B+ · sygnał za niski",
                "00532" => "Napięcie zasilania B+",
                _ => ""
            };
            if (translated.Length > 0) return translated;
        }
        return "";
    }

    static string FaultDescription(ReportFault fault)
    {
        var lines = fault.Details.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return "";
        const string codePrefix = @"^(?:[PCBU][0-9A-F]{4}\s*(?:-\s*)?)?(?:\d{2}\s*\[\d+\]|\d{2,3}(?:-\d{2})?)\s*-\s*";
        const string trailingStatus = @"\s+-\s+(?:Intermittent|Static|Sporadic)\s*$";
        string Clean(string line)
        {
            string detail = Regex.Replace(Regex.Replace(line, codePrefix, "", RegexOptions.IgnoreCase),
                trailingStatus, "", RegexOptions.IgnoreCase).Trim(' ', '-');
            return detail.Equals("Intermittent", StringComparison.OrdinalIgnoreCase) ? "" : detail;
        }
        string description = Clean(lines[0]);
        if (lines.Length > 1 && Regex.IsMatch(lines[1], codePrefix, RegexOptions.IgnoreCase))
        {
            string condition = Clean(lines[1]);
            if (condition.Length > 0) description = string.Join(": ", new[] { description, condition }.Where(value => value.Length > 0));
        }
        return description;
    }

    static Grid FaultRow(ReportFault fault, string arrow)
    {
        string known = KnownPolishFault(fault);
        string original = FaultDescription(fault);
        string local = known.Length > 0 ? known : DtcTranslations.Local(original);
        var row = FaultColumns(fault.Code, local.Length > 0 ? local :
            original.Length > 0 ? original + " · brak tłumaczenia" : "Brak opisu w raporcie",
            FaultController(fault), PolishStatus(fault), arrow);
        if (local.Length > 0 || original.Length == 0) return row;

        var description = (TextBlock)row.Children[1];
        description.Loaded += async (_, _) =>
        {
            string translated = await DtcTranslations.TranslateAsync(original);
            if (translated.Length > 0) description.Text = translated;
        };
        return row;
    }

    static string PolishStatus(ReportFault fault)
    {
        string detail = fault.Details;
        bool Has(string pattern) => Regex.IsMatch(detail, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var statuses = new List<string>();
        if (Has(@"\b(?:Intermittent|Sporadic|Sporadycz\w*|sporadisch)\b")) statuses.Add("Sporadyczny");
        else if (Has(@"\b(?:Static|Statyczn\w*|Stały|statisch)\b")) statuses.Add("Stały");
        if (Has(@"\b(?:Not Confirmed|Niepotwierdzon\w*|Unconfirmed)\b")) statuses.Add("Niepotwierdzony");
        else if (Has(@"\b(?:Confirmed|Potwierdzon\w*)\b")) statuses.Add("Potwierdzony");
        if (Has(@"\bMIL ON\b")) statuses.Add("Kontrolka MIL włączona");
        if (statuses.Count > 0) return string.Join(" · ", statuses);
        var raw = Regex.Match(detail, @"(?im)^(?:Fault Status|Status błędu)\s*:\s*([01]{8})\b");
        return raw.Success ? "Status VCDS " + raw.Groups[1].Value : "Nie podano";
    }

    static void AddScanField(StackPanel panel, string label, string value)
    {
        if (value.Length > 0) panel.Children.Add(Txt(label + " · " + value, 14));
    }
}