using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DiagScope2;

public partial class MainWindow
{
    Expander? vehicleCardDetails;
    Expander? vehicleScans;
    Border? vehicleIdentity;
    bool showVehicleCard;
    bool studyIsDraft;
    string vehicleSearch = "";

    void SetActiveStudy(Study next)
    {
        analysisCancel?.Cancel();
        StopFollow();
        foreach (var graph in windows.Values.ToArray()) graph.Close();
        study = next;
        studyIsDraft = false;
    }

    void BeginStudy(Vehicle vehicle)
    {
        if (!Persist()) return;
        var next = new Study { Vehicle = vehicle.Copy() };
        Store.Save(next);
        SetActiveStudy(next);
        ShowVehicleCard();
        StorageState.Text = "Rozpoczęto nowe badanie";
    }

    void OpenVehicle(Vehicle vehicle)
    {
        var latest = ReadStudies(vehicle).FirstOrDefault();
        if (latest is not null)
        {
            OpenVehicleStudy(latest.Id, vehicle);
            return;
        }
        if (study.Vehicle.ProfileId == vehicle.ProfileId)
        {
            ShowVehicleCard();
            return;
        }
        if (!Persist()) return;
        SetActiveStudy(new Study { Vehicle = vehicle.Copy() });
        studyIsDraft = true;
        ShowVehicleCard();
    }

    void ShowVehicleCard(bool expandDetails = false)
    {
        showVehicleCard = true;
        Navigate("Pojazdy");
        if (expandDetails) RevealVehicleSection(vehicleCardDetails);
        else if (vehicleIdentity is { } identity)
            BringVehicleSectionIntoView(identity);
    }

    void RevealVehicleSection(Expander? details)
    {
        if (details is null) return;
        details.IsExpanded = true;
        BringVehicleSectionIntoView(details);
    }

    void BringVehicleSectionIntoView(FrameworkElement details)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (details.IsVisible)
                details.BringIntoView(new Rect(0, 0, details.ActualWidth, Math.Min(32, details.ActualHeight)));
        }));
    }

    void OpenVehicleStudy(string id, Vehicle vehicle, string destination = "Pojazdy")
    {
        if (study.Id != id)
        {
            var next = Store.Load(Store.SessionPath(id));
            if (!Persist()) return;
            Store.Activate(next.Id);
            next.Vehicle.ProfileId = vehicle.ProfileId;
            if (next.Vehicle.Vin.Length == 0) next.Vehicle.Vin = vehicle.Vin;
            SetActiveStudy(next);
        }
        if (destination == "Pojazdy") ShowVehicleCard();
        else Navigate(destination);
    }

    UIElement SavedVehicles(List<VehicleProfile> profiles)
    {
        var search = new TextBox { Text = vehicleSearch, MaxLength = 100 };
        var label = Txt("Szukaj po marce, modelu lub VIN", 14);
        System.Windows.Automation.AutomationProperties.SetLabeledBy(search, label);
        var matches = new StackPanel();
        var list = new ScrollViewer { Content = matches, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Update()
        {
            matches.Children.Clear();
            var words = vehicleSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var profile in profiles)
            {
                var vehicle = profile.Vehicle;
                string searchable = $"{vehicle.Make} {vehicle.Model} {vehicle.Vin}";
                if (!words.All(word => searchable.Contains(word, StringComparison.CurrentCultureIgnoreCase))) continue;
                var open = Btn(profile.Label, () => OpenVehicle(vehicle));
                open.Content = Txt(profile.Label, 14);
                ((TextBlock)open.Content).Margin = new Thickness(0);
                System.Windows.Automation.AutomationProperties.SetName(open, profile.Label);
                matches.Children.Add(open);
            }
            if (matches.Children.Count == 0)
                matches.Children.Add(Txt(profiles.Count == 0 ? "Brak zapisanych pojazdów." : "Brak pojazdów pasujących do wyszukiwania.", 14));
        }
        search.TextChanged += (_, _) =>
        {
            vehicleSearch = search.Text;
            Update();
        };
        Update();
        return Card(Stack(Txt("Zapisane pojazdy", 20), label, search, list));
    }

    void EditStudyNotes()
    {
        var visit = study.Visit;
        var dialog = new FormDialog(this, "Notatki z badania");
        var mileage = dialog.Field("Przebieg [km]", visit.MileageKm?.ToString(CultureInfo.InvariantCulture) ?? "");
        var concern = dialog.Field("Zgłoszony problem", visit.Concern, true);
        var work = dialog.Field("Wykonane prace", visit.Work, true);
        var open = dialog.Field("Do zrobienia", visit.Open, true);
        dialog.Accept = () =>
        {
            int? km = null;
            if (mileage.Text.Trim().Length > 0)
            {
                if (!int.TryParse(mileage.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                    throw new ArgumentException("Wpisz przebieg w pełnych kilometrach.");
                km = parsed;
            }
            study.Visit = new(km, concern.Text.Trim(), work.Text.Trim(), open.Text.Trim());
            if (!Persist())
            {
                study.Visit = visit;
                throw new IOException("Zapis badania nie powiódł się.");
            }
        };
        if (dialog.ShowDialog() == true) Navigate("Pojazdy");
    }

    UIElement StudyHistory(Vehicle vehicle, IReadOnlyList<StudySummary> studies)
    {
        var content = Stack(Txt("Historia badań", 20));
        if (studies.Count == 0) content.Children.Add(Txt("Brak zapisanych badań.", 14));
        foreach (var item in studies)
        {
            var body = new StackPanel { Margin = new Thickness(12, 7, 0, 8) };
            var notes = item.Visit ?? new();
            AddScanField(body, "Przebieg", notes.MileageKm is int km ? $"{km:N0} km" : "");
            AddScanField(body, "Zgłoszenie", notes.Concern);
            AddScanField(body, "Wykonane", notes.Work);
            AddScanField(body, "Do zrobienia", notes.Open);
            foreach (var scan in item.AutoScans.OrderByDescending(scan => scan.Report.ScannedAt))
                AddScanField(body, scan.ImportSource.Length == 0 ? "Auto Scan" : "Odczyt DTC", ReadingLabel(scan));
            foreach (var log in item.Logs) AddScanField(body, "Log", log.Name);
            foreach (var entry in item.Entries)
            {
                AddScanField(body, "Wpis", entry.Title);
                AddScanField(body, "Wynik", entry.Outcome);
                AddScanField(body, "Notatka", entry.Note);
            }
            var actions = Row();
            if (item.Id != study.Id) actions.Children.Add(Btn("Otwórz badanie", () => OpenVehicleStudy(item.Id, vehicle)));
            else actions.Children.Add(Btn("Edytuj notatki", EditStudyNotes));
            if (item.Logs.Count > 0) actions.Children.Add(Btn("Pomiary", () => OpenVehicleStudy(item.Id, vehicle, "Pomiary")));
            if (item.Entries.Count > 0) actions.Children.Add(Btn("Wyniki", () => OpenVehicleStudy(item.Id, vehicle, "Wyniki")));
            body.Children.Add(actions);
            content.Children.Add(new Expander
            {
                Header = $"{item.Date:dd.MM.yyyy HH:mm:ss} · Auto Scan: {item.AutoScans.Count(scan => scan.ImportSource.Length == 0)} · odczyty DTC: {item.AutoScans.Count(scan => scan.ImportSource.Length > 0)} · DTC: {item.Dtcs.Count} · logi: {item.Logs.Count} · wpisy: {item.Entries.Count}" +
                    (item.Id == study.Id ? " · bieżące" : ""),
                Content = body,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }
        return Card(content);
    }

    internal sealed class StudySummary
    {
        public string Id { get; set; } = "";
        public DateTimeOffset Date { get; set; }
        public Vehicle Vehicle { get; set; } = new();
        public VisitNotes? Visit { get; set; }
        public List<SavedAutoScan> AutoScans { get; set; } = [];
        public List<Entry> Dtcs { get; set; } = [];
        public List<Entry> Entries { get; set; } = [];
        public List<StudyLog> Logs { get; set; } = [];
    }

    internal sealed class StudyLog
    {
        public string Name { get; set; } = "";
    }

    internal static List<StudySummary> ReadStudies(Vehicle vehicle, IReadOnlyList<VehicleProfile>? profiles = null)
    {
        var folder = Path.Combine(Store.Root, "Sessions");
        var results = new List<StudySummary>();
        if (!Directory.Exists(folder)) return results;
        profiles ??= VehicleProfiles.Read();
        string vin = LogMetadata.ValidVin(vehicle.Vin);
        bool uniqueVin = vin.Length > 0 && profiles.Count(profile => LogMetadata.ValidVin(profile.Vehicle.Vin) == vin) <= 1;
        var otherIds = profiles.Where(profile => profile.Vehicle.ProfileId != vehicle.ProfileId)
            .Select(profile => profile.Vehicle.ProfileId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var item = JsonSerializer.Deserialize<StudySummary>(input);
            if (item?.Vehicle is null || !string.Equals(Path.GetFileNameWithoutExtension(path), item.Id, StringComparison.OrdinalIgnoreCase)) continue;
            string studyVin = LogMetadata.ValidVin(item.Vehicle.Vin);
            bool sameId = vehicle.ProfileId.Length > 0 && string.Equals(vehicle.ProfileId, item.Vehicle.ProfileId, StringComparison.OrdinalIgnoreCase);
            if ((sameId && (vin.Length == 0 || studyVin.Length == 0 || studyVin == vin)) ||
                (uniqueVin && studyVin == vin && !otherIds.Contains(item.Vehicle.ProfileId)))
                results.Add(item);
        }
        return results.OrderByDescending(item => item.Date).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
    }

    static Expander VehicleModuleDetails(Vehicle vehicle)
    {
        var readings = vehicle.ModuleReadings.Count > 0 ? vehicle.ModuleReadings :
            vehicle.ScanModules.Select(module => new ModuleReading(vehicle.Source, "", vehicle.LastScanAt, module)).ToList();
        var details = new StackPanel { Margin = new Thickness(10, 8, 0, 0) };
        foreach (var group in readings.GroupBy(reading => reading.Module.Address).OrderBy(group => group.Key))
        {
            var ordered = group.Reverse().OrderByDescending(reading => reading.RecordedAt).ToArray();
            var records = new StackPanel();
            foreach (var reading in ordered)
            {
                var module = reading.Module;
                var fields = Stack(Txt(ScanDate(reading.RecordedAt), 15));
                AddScanField(fields, "Numer części / SW", module.Software);
                AddScanField(fields, "Numer części / HW", module.Hardware);
                AddScanField(fields, "Wersja oprogramowania", module.SoftwareVersion);
                AddScanField(fields, "Wersja sprzętu", module.HardwareVersion);
                AddScanField(fields, "Komponent", module.Component);
                AddScanField(fields, "Rewizja", module.Revision);
                AddScanField(fields, "Numer seryjny", module.Serial);
                AddScanField(fields, "Kodowanie", module.Coding);
                AddScanField(fields, "Plik etykiet", module.Labels);
                AddScanField(fields, "Kod warsztatu", module.WorkshopCode);
                AddScanField(fields, "VCID", module.Vcid);
                AddScanField(fields, "Dane ASAM", module.AsamDataset);
                AddScanField(fields, "Plik ROD", module.RodFile);
                AddScanField(fields, "Gotowość", module.Readiness);
                if (module.SubsystemDetails.Count > 0)
                    AddScanField(fields, "Podsystemy", string.Join("\n", module.SubsystemDetails));
                records.Children.Add(fields);
            }
            details.Children.Add(new Expander
            {
                Header = $"{group.Key} {PolishModule(group.Key)} · {ScanDate(ordered[0].RecordedAt)}",
                Content = records,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }
        return new Expander { Header = "Sterowniki i historia odczytów", Content = details };
    }
}