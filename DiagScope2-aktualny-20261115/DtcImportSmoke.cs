using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DiagScope2;

public partial class MainWindow
{
    internal async Task<string> CheckDtcImport(bool restart)
    {
        const string knownVin = "WAUZZZ4BZWN123456";
        const string description = "Steering Angle Sensor: No Signal";
        void Check(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason);
        }

        if (restart)
        {
            var vehicle = VehicleProfiles.Read().Single(profile => profile.Vehicle.Vin == knownVin).Vehicle;
            var history = ReadStudies(vehicle);
            Check(history.Count == 2 && study.Id == history[0].Id, "History or the selected study did not survive restart.");
            OpenVehicle(vehicle);
            Check(showVehicleCard && study.AutoScans.Count == 2 && study.Dtcs.Count == 2, "Vehicle card did not reopen with both readings.");
            Check(study.AutoScans[0].ImportSource == VcdsFileSource && study.AutoScans[1].ImportSource == VcdsClipboardSource,
                "Reading provenance did not survive restart.");
            Check(DtcTranslations.Local(description).Length > 0, "Online translation did not load from local cache after restart.");
            return "PASS restart: vehicle card, two studies, two readings, original archive and offline translation cache.";
        }

        var target = new Vehicle { Make = "Audi", Vin = knownVin };
        VehicleProfiles.Save(target);
        var earlier = new Study { Date = DateTimeOffset.Now.AddHours(-1), Vehicle = target.Copy() };
        earlier.Entries.Add(new Entry { Title = "Earlier result" });
        Store.Save(earlier);
        byte[] original = File.ReadAllBytes(Store.SessionPath(earlier));

        OpenVehicle(target);
        Check(((StackPanel)vehicleIdentity!.Child).Children.OfType<WrapPanel>()
            .SelectMany(row => row.Children.OfType<Button>()).Any(button => Equals(button.Content, "Importuj błędy VCDS")),
            "Import action is missing from the saved vehicle card.");
        Check(Equals(vehicleCardDetails!.Header, "Dane pojazdu"), "Parts selection is still exposed.");

        var path = Path.Combine(Store.Root, "Save-Codes-01.txt");
        File.WriteAllText(path, """
            Sunday,25,October,2026,20:59:33:16365
            VCDS Version: 23.3.1.0
            VIN: WAUZZZ4BZWN123456
            Address 01: Engine Labels: Test.lbl
            Control Module Part Number: 4B0 907 552    HW: 4B0 907 553
            Software Coding: 00101
            1 Fault Found:

            16514 - Oxygen Sensor
             P0130 - 35-10 - Circuit - Intermittent
            """);
        var file = VcdsFaultReading.FromFile(path);
        var fileReport = file.Reports.Single();
        var fileFault = fileReport.Faults.Single();
        Check(fileReport.ScannedAt?.Second == 33 && fileReport.Modules.Single().Coding == "00101" &&
            fileFault.Code == "16514 / P0130" && PolishStatus(fileFault) == "Sporadyczny" &&
            KnownPolishFault(fileFault).Contains("B1S1"), "The legacy TXT DTC, date, module, status or local translation did not parse.");
        Check(SaveVcdsFaultReading(target, file, null, false) == 1, "Matching VIN TXT could not start a new study.");
        string studyId = study.Id;

        var clipboardData = new DataObject(DataFormats.UnicodeText, """
            Address 03: ABS Brakes
            1 Fault Found:
            00123 - Steering Angle Sensor
             C1000 00 [008] - No Signal
             Intermittent - Not Confirmed
             Freeze Frame:
              Fault Status: 00000001
              Mileage: 200123 km
            """);
        var clipboard = VcdsFaultReading.FromClipboard(clipboardData);
        var fault = clipboard.Reports.Single().Faults.Single();
        Check(fault.Code == "00123 / C1000" && fault.Controller == "03 ABS Brakes" &&
            PolishStatus(fault) == "Sporadyczny · Niepotwierdzony" && FaultDescription(fault) == description &&
            fault.Details.Contains("200123 km"), "The clipboard DTC, description, module, status or freeze frame did not parse.");
        Check(RequiresVehicleConfirmation(target, clipboard), "A reading without VIN did not require confirmation.");
        try
        {
            SaveVcdsFaultReading(target, clipboard, studyId, false);
            throw new Exception("A reading without VIN was saved without confirmation.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Potwierdź")) { }

        var wrong = new Vehicle { Vin = "WVWZZZ9NZ6Y123456" };
        try
        {
            RequiresVehicleConfirmation(wrong, file);
            throw new Exception("VIN mismatch was accepted.");
        }
        catch (InvalidDataException) { }

        Check(SaveVcdsFaultReading(target, clipboard, studyId, true) == 1 && study.Id == studyId,
            "Confirmed clipboard reading could not be appended to the same study.");
        Check(SaveVcdsFaultReading(target, clipboard, studyId, true) == 0, "Identical repeat import was duplicated.");
        Check(study.AutoScans.Count == 2 && study.Dtcs.Count == 2 &&
            study.AutoScans.All(scan => scan.ImportedAt.HasValue) &&
            study.AutoScans.All(scan => File.Exists(ScanArchivePath(study, scan))) &&
            File.ReadAllBytes(Store.SessionPath(earlier)).SequenceEqual(original),
            "History, archived input, or source timestamps were not preserved.");
        Check(PolishModule("03") == "ABS" && VehicleReport.ReadAll("No fault code found.", true).Single().StandaloneFaultCount == 0,
            "A clean reading was not recognized.");
        var withoutAddress = VehicleReport.ReadAll(
            "Control Module Part Number: 8E0 614 517\n1 Fault Found:\n00532 - Supply Voltage B+\n  07-10 - Signal too Low", true).Single();
        Check(withoutAddress.Faults.Single().Controller == "8E0 614 517" && withoutAddress.Modules.Single().Address == "",
            "Standalone module identification without an address was not preserved.");
        Check(!DtcTranslations.IsShareableDescription("VIN WAUZZZ4BZWN123456") &&
            (await DtcTranslations.TranslateAsync("VIN WAUZZZ4BZWN123456")).Length == 0 &&
            ((TextBlock)FaultRow(new ReportFault("", "00120", "VIN WAUZZZ4BZWN123456"), "").Children[1]).Text.EndsWith("brak tłumaczenia"),
            "Translation did not protect identifiers or show the fallback.");
        string translated = await DtcTranslations.TranslateAsync(description);
        Check(translated.Length > 0 && translated != description && DtcTranslations.Local(description) == translated &&
            File.Exists(Path.Combine(Store.Root, "DtcTranslations.json")), "Live translation or persistent cache failed.");
        ShowVehicleCard();
        return "PASS import: saved card action, TXT engine, clipboard data ABS, matching/missing/mismatching VIN, preserved history and archives, known/local and live translations, original fallback.";
    }
}