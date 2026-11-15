using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DiagScope2;

public partial class MainWindow
{
    const string VcdsFileSource = "Plik TXT VCDS";
    const string VcdsClipboardSource = "Schowek VCDS";

    sealed record VcdsFaultReading(string Name, string Source, byte[] Bytes, List<VehicleReport> Reports)
    {
        public static VcdsFaultReading FromFile(string path)
        {
            var bytes = Importer.ReadBytes(path);
            string text = bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())
                ? Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF')
                : bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())
                    ? Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF')
                    : Importer.Decode(bytes);
            return Parse(Path.GetFileName(path), VcdsFileSource, bytes, text);
        }

        public static VcdsFaultReading FromClipboard(IDataObject? clipboard)
        {
            if (clipboard?.GetData(DataFormats.UnicodeText, true) is not string text || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("W schowku nie ma tekstu. Użyj „Copy Codes” w VCDS.");
            return Parse(VcdsClipboardSource, VcdsClipboardSource, Encoding.UTF8.GetBytes(text), text);
        }

        static VcdsFaultReading Parse(string name, string source, byte[] bytes, string text)
        {
            var reports = VehicleReport.ReadAll(text.TrimStart('\uFEFF').ReplaceLineEndings("\n"), standaloneFaultReading: true);
            if (reports.Count == 0)
                throw new InvalidDataException("Brak rozpoznanego odczytu DTC. Użyj „Save Codes” lub „Copy Codes” w VCDS.");
            return new(name, source, bytes, reports);
        }
    }

    void ImportVcdsFaults(Vehicle vehicle)
    {
        var dialog = new FormDialog(this, "Importuj błędy VCDS") { Height = 500 };
        var studies = ReadStudies(vehicle);
        VcdsFaultReading? reading = null;
        var preview = Txt("Wybierz plik zapisany przez „Save Codes” lub skopiuj odczyt przez „Copy Codes”.", 14);
        var confirmation = new CheckBox
        {
            Foreground = dialog.Foreground,
            Content = "Potwierdzam przypisanie odczytu bez VIN do tego pojazdu",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 10)
        };
        void LoadReading(Func<VcdsFaultReading> load)
        {
            reading = null;
            confirmation.IsChecked = false;
            confirmation.Visibility = Visibility.Collapsed;
            preview.Text = "Nie wczytano odczytu.";
            var parsed = load();
            bool needsConfirmation = RequiresVehicleConfirmation(vehicle, parsed);
            var controllers = parsed.Reports.SelectMany(report => report.Modules)
                .Select(VehicleReport.ControllerLabel).Where(name => name.Length > 0).Distinct().ToArray();
            preview.Text = $"{parsed.Name} · odczyty: {parsed.Reports.Count} · odczytane kody: {parsed.Reports.Sum(report => report.Faults.Count)}" +
                (controllers.Length > 0 ? "\nSterowniki: " + string.Join(", ", controllers) : "\nNie podano sterownika w raporcie.");
            confirmation.Visibility = needsConfirmation ? Visibility.Visible : Visibility.Collapsed;
            reading = parsed;
        }

        dialog.Body.Children.Add(Txt(new VehicleProfile(vehicle).Label, 16));
        dialog.Body.Children.Add(Row(Btn("Wczytaj plik", () =>
        {
            var picker = new OpenFileDialog { Filter = "Raport VCDS (*.txt)|*.txt", InitialDirectory = VcdsLogsDirectory() };
            if (picker.ShowDialog(dialog) == true) LoadReading(() => VcdsFaultReading.FromFile(picker.FileName));
        }), Btn("Wklej ze schowka", () => LoadReading(() => VcdsFaultReading.FromClipboard(Clipboard.GetDataObject())))));
        dialog.Body.Children.Add(preview);
        dialog.Body.Children.Add(confirmation);
        var choices = studies.Select(item => $"{item.Date:dd.MM.yyyy HH:mm:ss}" + (item.Id == study.Id ? " · bieżące" : ""))
            .Prepend("Nowe badanie").ToArray();
        int activeIndex = studies.FindIndex(item => item.Id == study.Id) + 1;
        var destination = dialog.Select("Zapisz odczyt w badaniu", choices, choices[activeIndex]);
        destination.SelectedIndex = activeIndex;
        dialog.Accept = () =>
        {
            if (reading is null) throw new InvalidOperationException("Najpierw wczytaj odczyt VCDS.");
            string? studyId = destination.SelectedIndex > 0 ? studies[destination.SelectedIndex - 1].Id : null;
            int added = SaveVcdsFaultReading(vehicle, reading, studyId, confirmation.IsChecked == true);
            ShowVehicleCard();
            RevealVehicleSection(vehicleScans);
            StorageState.Text = added == 0 ? "Ten odczyt jest już w wybranym badaniu" : "Zapisano odczyt błędów VCDS";
        };
        dialog.ShowDialog();
    }

    static bool RequiresVehicleConfirmation(Vehicle vehicle, VcdsFaultReading reading)
    {
        var vins = reading.Reports.Select(report => report.Vehicle.Vin).Where(vin => vin.Length > 0).Distinct().ToArray();
        string cardVin = LogMetadata.ValidVin(vehicle.Vin);
        if (reading.Reports.Any(report => report.AmbiguousVin) || vins.Length > 1)
            throw new InvalidDataException("Raport zawiera różne numery VIN. Wczytaj odczyt jednego pojazdu.");
        if (cardVin.Length > 0 && vins.Length == 1 && vins[0] != cardVin)
            throw new InvalidDataException("VIN raportu różni się od VIN otwartej karty. Otwórz kartę właściwego pojazdu.");
        return cardVin.Length == 0 || reading.Reports.Any(report => report.Vehicle.Vin.Length == 0);
    }

    int SaveVcdsFaultReading(Vehicle vehicle, VcdsFaultReading reading, string? studyId, bool confirmedVehicle)
    {
        var profiles = VehicleProfiles.Read();
        vehicle = profiles.FirstOrDefault(profile => profile.Vehicle.ProfileId == vehicle.ProfileId)?.Vehicle
            ?? throw new InvalidOperationException("Najpierw zapisz kartę pojazdu.");
        if (RequiresVehicleConfirmation(vehicle, reading) && !confirmedVehicle)
            throw new InvalidOperationException("Potwierdź, że odczyt dotyczy otwartego pojazdu.");
        if (studyId is not null && !ReadStudies(vehicle, profiles).Any(item => item.Id == studyId))
            throw new InvalidOperationException("Wybrane badanie nie należy do tego pojazdu.");
        if (!Persist()) throw new IOException("Zapis bieżącego badania nie powiódł się.");
        var now = DateTimeOffset.Now;
        var target = studyId is null ? new Study { Vehicle = vehicle.Copy(), Date = now } : Store.Load(Store.SessionPath(studyId));
        string hash = Convert.ToHexString(SHA256.HashData(reading.Bytes));
        var additions = reading.Reports.Select((report, index) => new SavedAutoScan(reading.Name, hash, report)
        {
            Section = index,
            ImportedAt = now,
            ImportSource = reading.Source
        }).Where(scan => !target.AutoScans.Any(saved =>
            saved.Hash == hash && saved.Section == scan.Section && saved.ImportSource == scan.ImportSource)).ToArray();
        if (additions.Length == 0) return 0;

        foreach (var scan in additions)
        {
            string archive = ScanArchivePath(target, scan);
            if (!File.Exists(archive)) Store.WriteAtomic(archive, reading.Bytes);
            target.AutoScans.Add(scan);
            target.Dtcs.AddRange(scan.Report.Faults.Select(fault => new Entry
            {
                Date = now,
                Title = FaultTitle(fault),
                Source = scan.Name,
                Status = PolishStatus(fault),
                Note = fault.Details
            }));
        }
        Store.Save(target);
        if (study.Id == target.Id)
        {
            study = target;
            studyIsDraft = false;
        }
        else SetActiveStudy(target);
        foreach (var fault in additions.SelectMany(scan => scan.Report.Faults))
            if (KnownPolishFault(fault).Length == 0) _ = DtcTranslations.TranslateAsync(FaultDescription(fault));
        return additions.Length;
    }

    static string ReadingLabel(SavedAutoScan scan) =>
        scan.ImportSource.Length == 0 ? ScanDate(scan.Report.ScannedAt) :
        $"{scan.ImportedAt:dd.MM.yyyy HH:mm:ss} · {scan.ImportSource}" +
        (scan.ImportSource == VcdsFileSource ? " · " + scan.Name : "");
}