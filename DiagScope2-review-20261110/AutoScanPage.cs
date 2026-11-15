using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DiagScope2;

public partial class MainWindow
{
    const string AutoScanTab = "Auto-Scan";

    void PickAutoScan()
    {
        var picker = new OpenFileDialog
        {
            Filter = "Raport Auto-Scan (*.txt;*.log)|*.txt;*.log",
            InitialDirectory = VcdsLogsDirectory()
        };
        if (picker.ShowDialog() == true) ImportReport(picker.FileName);
    }

    void ImportReport(string path)
    {
        byte[] bytes = Importer.ReadBytes(path);
        var report = VehicleReport.Read(Importer.Decode(bytes));
        if (!report.Recognized)
            throw new FormatException("Nie rozpoznano raportu Auto-Scan. Wybierz tekstowy raport zapisany w VCDS.");

        var scan = new SavedAutoScan(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)), report);
        SaveAutoScan(scan, bytes);
        Navigate(AutoScanTab);
    }

    void SaveAutoScan(SavedAutoScan scan, byte[] bytes)
    {
        var report = scan.Report;
        string vin = report.Vehicle.Vin;
        if (study.AutoScans.Any(saved => saved.Hash == scan.Hash)) return;

        bool sameVehicle = vin.Length > 0 && LogMetadata.ValidVin(study.Vehicle.Vin) == vin;
        bool emptyStudy = study.Vehicle.ProfileId.Length == 0 && study.Vehicle.Vin.Length == 0 &&
            study.Vehicle.Make.Length == 0 && study.Vehicle.Model.Length == 0 && study.Vehicle.Engine.Length == 0 &&
            study.Vehicle.Capacity.Length == 0 && study.Vehicle.HasLpg is null && study.Entries.Count == 0 &&
            study.Logs.Count == 0 && study.Dtcs.Count == 0 && study.AutoScans.Count == 0 && study.Checks.Count == 0;
        bool switchStudy = !sameVehicle && !emptyStudy;
        if (switchStudy) Store.Save(study);
        var target = switchStudy ? new Study() : study;

        var profile = vin.Length == 0 ? null : VehicleProfiles.Read().FirstOrDefault(item => item.Vehicle.Vin == vin);
        var oldVehicle = target.Vehicle;
        var vehicle = (profile?.Vehicle ?? oldVehicle).Copy();
        vehicle.Vin = vin;
        if (vehicle.Make.Length == 0) vehicle.Make = report.Vehicle.Make;
        if (vehicle.Model.Length == 0) vehicle.Model = report.Vehicle.Model;
        if (vehicle.Engine.Length == 0) vehicle.Engine = report.Vehicle.Engine;
        if (vehicle.Capacity.Length == 0) vehicle.Capacity = report.Vehicle.Capacity;
        if (vehicle.Chassis.Length == 0) vehicle.Chassis = report.Chassis;
        if (vehicle.Source.Length == 0) vehicle.Source = scan.Name;
        if (!vehicle.LastScanAt.HasValue || report.ScannedAt >= vehicle.LastScanAt)
        {
            vehicle.LastScanAt = report.ScannedAt;
            vehicle.LastScanMileageKm = report.MileageKm;
            vehicle.ScanModules = report.Modules;
        }

        string archive = ScanArchivePath(target, scan);
        if (!File.Exists(archive)) Store.WriteAtomic(archive, bytes);
        if (vin.Length > 0) VehicleProfiles.Save(vehicle);

        var addedFaults = report.Faults.Select(fault => new Entry
        {
            Title = string.Join(" · ", new[] { fault.Code, fault.Controller }.Where(value => value.Length > 0)),
            Source = scan.Name,
            Status = Statuses[2],
            Note = fault.Details
        }).ToList();
        target.Vehicle = vehicle;
        target.AutoScans.Add(scan);
        target.Dtcs.AddRange(addedFaults);
        try { Store.Save(target); }
        catch
        {
            target.Vehicle = oldVehicle;
            target.AutoScans.Remove(scan);
            foreach (var fault in addedFaults) target.Dtcs.Remove(fault);
            throw;
        }

        if (switchStudy)
        {
            StopFollow();
            foreach (var graph in windows.Values.ToArray()) graph.Close();
            study = target;
        }
        StorageState.Text = "Zapisano Auto-Scan";
    }

    static string ScanArchivePath(Study owner, SavedAutoScan scan)
    {
        if (!Regex.IsMatch(scan.Hash, "^[0-9A-F]{64}$"))
            throw new InvalidDataException("Nieprawidłowy identyfikator skanu.");
        string vin = LogMetadata.ValidVin(scan.Report.Vehicle.Vin);
        return Path.Combine(Store.Root, "Vehicles", vin.Length > 0 ? vin : owner.Id, "AutoScan", scan.Hash + ".txt");
    }

    void AutoScanPage()
    {
        Heading(AutoScanTab);
        var import = Btn("Wczytaj Auto-Scan (.txt)", PickAutoScan);
        import.Background = B("#183B60");
        import.BorderBrush = B("#74BEFF");
        Page.Children.Add(Row(import, Btn("Wpisz kod ręcznie", AddDtcManually)));

        foreach (var scan in study.AutoScans.AsEnumerable().Reverse()) ShowAutoScan(scan);
        if (study.AutoScans.Count == 0 && study.Dtcs.Count == 0)
            Page.Children.Add(Txt("Wybierz raport zapisany w VCDS, żeby odczytać dane pojazdu i sterowników."));

        if (study.Dtcs.Count == 0) return;
        Page.Children.Add(Txt("Błędy w badaniu", 21));
        foreach (var entry in study.Dtcs)
            Page.Children.Add(Card(Stack(Txt(entry.Title, 21), Txt(entry.Note),
                Txt($"{entry.Status} · {entry.Source} · wpis {entry.Date:dd.MM.yyyy HH:mm}", 13),
                Btn("Zapisz następny test", () => AddDtcTest(entry)))));
    }

    void ShowAutoScan(SavedAutoScan scan)
    {
        var report = scan.Report;
        var vehicle = report.Vehicle;
        string name = string.Join(" ", new[] { vehicle.Make, vehicle.Model }.Where(value => value.Length > 0));
        string identity = name.Length > 0 ? name : "Raport Auto-Scan";
        if (report.Chassis.Length > 0) identity += " · " + report.Chassis;
        var panel = Stack(Txt(identity, 22), Txt(scan.Name, 13));
        AddScanField(panel, "VIN", vehicle.Vin);
        AddScanField(panel, "Numer rejestracyjny", report.LicensePlate);
        AddScanField(panel, "Data skanu", report.ScannedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "");
        AddScanField(panel, "Przebieg", report.MileageKm is int km ? $"{km:N0} km" : "");
        if (vehicle.Vin.Length == 0) panel.Children.Add(Txt("Brak rozpoznanego VIN-u. Skan zapisano bez przypisywania do karty pojazdu.", 14));

        panel.Children.Add(Txt($"Sterowniki z wynikiem: {report.Modules.Count}", 19));
        foreach (var fault in report.Faults)
        {
            panel.Children.Add(Txt(string.Join(" · ", new[] { fault.Code, fault.Controller }.Where(value => value.Length > 0)), 16));
            panel.Children.Add(Txt(fault.Details, 14));
        }

        int clear = report.Modules.Count(module => module.ReportedFaults == 0);
        if (clear > 0) panel.Children.Add(Txt($"Sterowniki, które zgłosiły brak błędów: {clear}", 14));
        if (report.Modules.Any(module => module.ReportedFaults > 0) && report.Faults.Count == 0)
            panel.Children.Add(Txt("Raport zgłasza błędy, ale importer nie rozpoznał ich kodów.", 14));
        var missing = report.RequestedAddresses.Except(report.Modules.Select(module => module.Address)).ToArray();
        if (missing.Length > 0)
            panel.Children.Add(Txt("Adresy z listy skanowania bez wyniku w raporcie: " + string.Join(", ", missing), 14));
        if (report.Modules.Count > 0) panel.Children.Add(ModuleDetails(report.Modules));
        if (report.Version.Length > 0 || report.EndDetails.Length > 0)
        {
            var metadata = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            AddScanField(metadata, "Wersja VCDS", report.Version);
            AddScanField(metadata, "Zakończenie skanu", report.EndDetails);
            panel.Children.Add(new Expander { Header = "Informacje o skanie", Content = metadata, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
        }

        panel.Children.Add(Btn("Otwórz oryginał TXT", () => Process.Start(new ProcessStartInfo(ScanArchivePath(study, scan)) { UseShellExecute = true })));
        Page.Children.Add(Card(panel));
    }

    static Expander ModuleDetails(IEnumerable<VcdsModule> modules, string title = "Szczegóły sterowników")
    {
        var details = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var module in modules)
        {
            string status = module.ReportedFaults switch { 0 => "brak błędów", int count => $"zgłoszone błędy: {count}", _ => "brak statusu w raporcie" };
            details.Children.Add(Txt($"{module.Address} · {module.Name} · {status}", 15));
            AddScanField(details, "Numer części / SW", module.Software);
            AddScanField(details, "Numer HW", module.Hardware);
            AddScanField(details, "Komponent", module.Component);
            AddScanField(details, "Rewizja", module.Revision);
            AddScanField(details, "Kodowanie", module.Coding);
            AddScanField(details, "Plik etykiet", module.Labels);
        }
        return new Expander { Header = title, Content = details, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
    }

    static void AddScanField(StackPanel panel, string label, string value)
    {
        if (value.Length > 0) panel.Children.Add(Txt(label + " · " + value, 14));
    }

    void AddDtcManually()
    {
        var dialog = new FormDialog(this, "Wpisz kod ręcznie");
        var code = dialog.Field("Kod DTC");
        var ecu = dialog.Field("Sterownik / adres");
        var note = dialog.Field("Opis, stan błędu i freeze frame", multiline: true);
        var source = dialog.Field("Źródło i data odczytu");
        var status = dialog.Select("Status", Statuses);
        dialog.Accept = () =>
        {
            if (string.IsNullOrWhiteSpace(code.Text) || string.IsNullOrWhiteSpace(source.Text))
                throw new ArgumentException("Wpisz kod i źródło.");
            study.Dtcs.Add(new()
            {
                Title = string.Join(" · ", new[] { code.Text.Trim(), ecu.Text.Trim() }.Where(value => value.Length > 0)),
                Source = source.Text.Trim(),
                Note = note.Text,
                Status = status.Text
            });
            Persist();
        };
        if (dialog.ShowDialog() == true) Navigate(AutoScanTab);
    }
}