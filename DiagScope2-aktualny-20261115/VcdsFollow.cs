using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DiagScope2;

public partial class MainWindow
{
    readonly Dictionary<LogWindow, WindowState> minimizedGraphs = new();
    DispatcherTimer? vcdsDiscoveryTimer;
    LogData? followedLog;
    bool automaticFollow;
    bool pendingLiveChart;
    TextBlock? followStatus;
    Button? saveFollowed;
    Button? showFollowed;

    static string AutoFollowSetting => Path.Combine(Store.Root, "vcds-auto-follow.txt");
    static readonly string[] VcdsFolders = [@"C:\Ross-Tech\VCDS\Logs", @"C:\Ross-Tech\VCDS-DRV\Logs"];

    void MinimizeApp(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            foreach (var graph in windows.Values.Where(w => w.WindowState != WindowState.Minimized))
            {
                minimizedGraphs[graph] = graph.WindowState;
                graph.WindowState = WindowState.Minimized;
            }
            return;
        }

        foreach (var (graph, state) in minimizedGraphs.ToArray())
            if (graph.WindowState == WindowState.Minimized) graph.WindowState = state;
        minimizedGraphs.Clear();
        if (pendingLiveChart && followedLog is { } current)
        {
            pendingLiveChart = false;
            OpenLogWindow(current, activate: false);
        }
    }

    void LoadVcdsAutoFollowSetting()
    {
        try
        {
            if (File.Exists(AutoFollowSetting) && File.ReadAllText(AutoFollowSetting).Trim() == "on")
                StartVcdsDiscovery(VcdsFolders);
        }
        catch (IOException ex) { StorageState.Text = "Ustawienia VCDS: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { StorageState.Text = "Ustawienia VCDS: " + ex.Message; }
    }

    void ChangeVcdsAutoFollow(CheckBox control, bool enabled)
    {
        try
        {
            Store.WriteAtomic(AutoFollowSetting, enabled ? "on" : "off");
            if (enabled) StartVcdsDiscovery(VcdsFolders);
            else
            {
                StopVcdsDiscovery();
                if (automaticFollow) StopFollow();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ustawienia VCDS");
            Navigate("Pomiary");
        }
        UpdateFollowStatus();
    }

    void StartVcdsDiscovery(string[] folders)
    {
        StopVcdsDiscovery();
        var started = DateTime.UtcNow;
        var knownFiles = ReadVcdsFiles(folders);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        vcdsDiscoveryTimer = timer;
        bool scanning = false;
        timer.Tick += async (_, _) =>
        {
            if (scanning || vcdsDiscoveryTimer != timer) return;
            scanning = true;
            try
            {
                var latest = await Task.Run(() => ReadVcdsFiles(folders));
                if (vcdsDiscoveryTimer != timer) return;
                var changed = latest
                    .Where(file => file.Value.Size > 0 && (knownFiles.TryGetValue(file.Key, out var old)
                        ? old != file.Value : file.Value.Modified >= started.AddSeconds(-2)))
                    .OrderByDescending(file => file.Value.Modified)
                    .ToArray();
                knownFiles = latest;
                if (followTimer != null && !automaticFollow) return;
                if (changed.Any(file => file.Key.Equals(followPath, StringComparison.OrdinalIgnoreCase))) return;

                foreach (var file in changed.Take(8))
                {
                    if (file.Key.Equals(followPath, StringComparison.OrdinalIgnoreCase)) continue;
                    LogData log;
                    try { log = await Task.Run(() => Importer.Read(file.Key)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException || ex is InvalidDataException) { continue; }
                    if (vcdsDiscoveryTimer != timer || (followTimer != null && !automaticFollow)) return;
                    if (log.Source != "VCDS CSV") continue;
                    FollowLog(file.Key, automatic: true, log, file.Value);
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (vcdsDiscoveryTimer == timer) StorageState.Text = "VCDS: " + ex.Message;
            }
            finally { scanning = false; }
        };
        timer.Start();
        UpdateFollowStatus();
    }

    static Dictionary<string, (long Size, DateTime Modified)> ReadVcdsFiles(IEnumerable<string> folders)
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*.csv"))
                {
                    // Directory listings can report a stale size while VCDS has the file open.
                    var file = new FileInfo(path);
                    if (file.Exists) files[path] = (file.Length, file.LastWriteTimeUtc);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file may disappear during a scan; other folders are still useful.
            }
        }
        return files;
    }

    void StopVcdsDiscovery()
    {
        vcdsDiscoveryTimer?.Stop();
        vcdsDiscoveryTimer = null;
    }

    void StartFollow()
    {
        var pick = new OpenFileDialog { Filter = "VCDS / DiagScope|*.csv;*.jsonl", InitialDirectory = VcdsLogsDirectory() };
        if (pick.ShowDialog() == true) FollowLog(pick.FileName, automatic: false);
    }

    void FollowLog(string path, bool automatic, LogData? initial = null, (long Size, DateTime Modified)? initialStamp = null)
    {
        StopFollow();
        followPath = path;
        automaticFollow = automatic;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        followTimer = timer;
        var lastRead = initialStamp;
        bool busy = false;
        if (initial != null) PublishFollowedLog(initial, automatic);

        async void ReadLatest(object? sender, EventArgs e)
        {
            if (busy || followTimer != timer) return;
            busy = true;
            try
            {
                var info = new FileInfo(path);
                var version = (info.Length, info.LastWriteTimeUtc);
                if (lastRead == version) return;
                var log = await Task.Run(() => Importer.Read(path));
                if (followTimer != timer) return;
                lastRead = version;
                PublishFollowedLog(log, automatic);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException || ex is InvalidDataException)
            {
                if (followTimer == timer) StorageState.Text = "Obserwacja: " + ex.Message;
            }
            finally { busy = false; }
        }

        timer.Tick += ReadLatest;
        timer.Start();
        UpdateFollowStatus();
        if (initial == null) ReadLatest(null, EventArgs.Empty);
    }

    void PublishFollowedLog(LogData log, bool automatic)
    {
        if (followedLog is not { } current)
        {
            log.Name = "Obserwacja · " + log.Name;
            followedLog = log;
            if (WindowState == WindowState.Minimized) pendingLiveChart = true;
            else OpenLogWindow(log, activate: !automatic);
        }
        else
        {
            foreach (var channel in log.Channels)
                channel.Visible = current.Channels.FirstOrDefault(old =>
                    old.Address == channel.Address && old.Name == channel.Name && old.Clock == channel.Clock)?.Visible ?? true;
            current.Channels = log.Channels;
            current.Rows = log.Rows;
            current.Warnings = log.Warnings;
            current.Markers.RemoveAll(marker => marker.IsAutomatic);
            current.Markers.AddRange(log.Markers.Where(marker => marker.IsAutomatic));
            if (windows.TryGetValue(current.Id, out var graph)) graph.Refresh();
        }
        StorageState.Text = $"{log.Source} · {DateTime.Now:HH:mm:ss}";
        UpdateFollowStatus();
    }

    void StopFollow()
    {
        followTimer?.Stop();
        followTimer = null;
        followPath = "";
        followedLog = null;
        automaticFollow = false;
        pendingLiveChart = false;
        StorageState.Text = "Obserwacja pliku zatrzymana";
        UpdateFollowStatus();
    }

    void StopFollowFromMeasurements()
    {
        if (automaticFollow)
        {
            Store.WriteAtomic(AutoFollowSetting, "off");
            StopVcdsDiscovery();
        }
        StopFollow();
        Navigate("Pomiary");
    }

    void UpdateFollowStatus()
    {
        if (followStatus == null) return;
        followStatus.Text = followPath.Length > 0
            ? $"{(automaticFollow ? "Wykryto" : "Obserwacja")}: {Path.GetFileName(followPath)} · {followedLog?.Rows ?? 0} wierszy"
            : vcdsDiscoveryTimer != null ? "Czeka na Log → Start w VCDS." : "Wybierz plik lub włącz automatyczne wykrywanie.";
        if (saveFollowed != null) saveFollowed.IsEnabled = followedLog != null;
        if (showFollowed != null) showFollowed.IsEnabled = followedLog != null;
    }
}