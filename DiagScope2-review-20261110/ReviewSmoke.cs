using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace DiagScope2;
public partial class App {
 internal void ReviewSmoke() {
  Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async ()=>{
   string dir=Environment.GetEnvironmentVariable("DIAGSCOPE_DATA_ROOT")!;
   try {
    Directory.CreateDirectory(Path.Combine(dir,"ui"));
    var window=(MainWindow)MainWindow;
    var flags=BindingFlags.NonPublic|BindingFlags.Instance;
    var study=(Study)typeof(MainWindow).GetField("study",flags)!.GetValue(window)!;
    var real=Importer.Read(@"C:\Dev\DiagScope2.Tests\fixtures\real-vcds.csv");
    if(real.RecordedLocal is not { } start||start.Year!=2026||!LogMetadata.FileStem(real).StartsWith(start.ToString("yyyy-MM-dd_HH-mm-ss")))
     throw new Exception("VCDS measurement date was not preserved.");
    if(!real.Channels.Any(c=>c.OriginalName.Length>0)||real.Channels.Any(c=>c.Name.StartsWith("Pole ")))
     throw new Exception("VCDS labels were not imported.");
    var firstProbe=real.Channels.Single(c=>c.Address=="033.2");
    var secondProbe=real.Channels.Single(c=>c.Address=="033.4");
    if(firstProbe.Name!="Napięcie sondy B1S1"||secondProbe.Name!="Napięcie sondy B2S1")
     throw new Exception("Verified ALF labels were not applied to the VCDS log.");
    CheckSavedChannelLabels(dir);
    var example=VehicleReport.Read("VIN: WAUZZZ4BZWN123456\nAddress 01: Engine\n1 Fault Found:\n16514 - Oxygen Sensor\nP0130 - 35-00 - Circuit");
    if(example.Vehicle.Vin.Length!=17||example.Faults.Count!=1||!example.Faults[0].Code.Contains("P0130"))
     throw new Exception("VCDS report data or grouped codes were not imported.");
    study.Logs.Add(real);
    var nav=typeof(MainWindow).GetMethod("Navigate",flags)!;
    foreach(string page in new[]{"Auto-Scan","Pojazdy","Pomiary","Diagnostyka","Wyniki","Ustawienia","Analizator logów"}) {
     nav.Invoke(window,[page]);window.UpdateLayout();Capture(window,Path.Combine(dir,"ui",page+".png"));
    }
    var panel=(StackPanel)window.FindName("Page");
    IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject {
     for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){
      var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;
      foreach(var item in Children<T>(child))yield return item;
     }
    }
    nav.Invoke(window,["Diagnostyka"]);window.UpdateLayout();
    if(Children<TextBlock>(panel).Any(t=>t.Text=="Instalacja LPG"))throw new Exception("LPG visible without vehicle opt-in.");
    study.Vehicle.HasLpg=true;
    nav.Invoke(window,["Diagnostyka"]);window.UpdateLayout();
    if(!Children<TextBlock>(panel).Any(t=>t.Text=="Instalacja LPG"))throw new Exception("LPG missing after vehicle opt-in.");
    nav.Invoke(window,["Analizator logów"]);window.UpdateLayout();
    var button=Children<Button>(panel).Single(b=>(b.Content as string)=="Analizuj");
    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    var list=Children<ListBox>(panel).Single();
    for(int i=0;i<100&&list.Items.Count==0;i++)await Task.Delay(100);
    if(list.Items.Count==0)throw new Exception("Analyzer produced no UI findings.");
    window.UpdateLayout();Capture(window,Path.Combine(dir,"ui","Analizator-wyniki.png"));
    var plot=Children<Button>(panel).Single(b=>(b.Content as string)=="Pokaż wniosek na wykresie");
    plot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    var open=typeof(MainWindow).GetMethod("OpenLog",flags)!;open.Invoke(window,[real]);open.Invoke(window,[real]);
    var graphs=Windows.OfType<LogWindow>().ToArray();
    if(graphs.Length!=1)throw new Exception("Duplicate log windows.");
    graphs[0].UpdateLayout();
    if(!Children<TextBlock>(graphs[0]).Any(t=>t.Text=="Napięcie sondy B1S1 [V]")||
       Children<TextBlock>(graphs[0]).Any(t=>t.Text.StartsWith("Pole ")))
     throw new Exception("The graph legend does not prioritize verified parameter names.");
    Capture(graphs[0],Path.Combine(dir,"ui","Wykres.png"));graphs[0].Close();
    await CheckVcdsDiscovery(window,dir,real,flags);
    CheckAutoScan(window,dir,flags);
    window.UpdateLayout();Capture(window,Path.Combine(dir,"ui","Auto-Scan-import.png"));
    File.WriteAllText(Path.Combine(dir,"ui-result.txt"),"PASS: Auto-Scan parsing, vehicle profile, original TXT, study isolation, 7 tabs, LPG opt-in, verified VCDS labels, and CSV detection while minimized.");
    Shutdown(0);
   }catch(Exception ex){File.WriteAllText(Path.Combine(dir,"ui-error.txt"),ex.ToString());Shutdown(2);}
  }));
 }

 static void CheckAutoScan(MainWindow window,string root,BindingFlags flags)
 {
  string? realPath=Environment.GetEnvironmentVariable("DIAGSCOPE_AUTOSCAN_TEST_PATH");
  string path=realPath??Path.Combine(root,"polo-example.txt");
  if(realPath is null)File.WriteAllText(path,"""
    Sunday,25,October,2026,20:59:33:16365
    VCDS Version: 23.3.1.0
    VIN: WVWZZZ9NZ6Y123456   License Plate:
    Mileage: 200490km-124578mi   Repair Order:
    Chassis Type: 9N (9N - VW Polo (2002 > 2010))
    Scan: 01 03 08 15 17 44
    Address 03: ABS Brakes        Labels: 6R0-907-37x-ABS80.clb
       Part No SW: 6Q0 907 379 AF    HW: 6Q0 907 379 AF
       Coding: 0002169
    No fault code found.
    Address 08: Auto HVAC
    No fault code found.
    Address 15: Airbags
    No fault code found.
    Address 17: Instruments
    No fault code found.
    Address 44: Steering Assist
    No fault code found.
    End----(Elapsed Time: 02:29)------
    """);
  var type=typeof(MainWindow);
  var current=type.GetField("study",flags)!;
  var previous=new Study{Vehicle=new Vehicle{Vin="WAUZZZ4BZWN123456",Make="Audi",HasLpg=true}};
  current.SetValue(window,previous);
  var import=type.GetMethod("ImportReport",flags)!;
  import.Invoke(window,[path]);
  import.Invoke(window,[path]);
  var saved=Store.Startup();
  var scan=saved.AutoScans.Single();
  var profile=VehicleProfiles.Read().Single().Vehicle;
  var report=scan.Report;
  var abs=report.Modules.Single(m=>m.Address=="03");
  if(profile.Vin.Length!=17||profile.Make!="VW"||profile.Model!="Polo"||profile.Chassis!="9N"||
     profile.LastScanMileageKm!=200490||profile.LastScanAt!=new DateTime(2026,10,25,20,59,33)||
     profile.HasLpg.HasValue||profile.Engine.Length>0||report.Faults.Count!=0||report.Modules.Count!=5||
     report.Modules.Any(m=>m.ReportedFaults!=0)||!report.RequestedAddresses.Contains("01")||
     report.Modules.Any(m=>m.Address=="01")||abs.Software!="6Q0 907 379 AF"||abs.Coding!="0002169")
   throw new Exception("Auto-Scan did not populate the vehicle profile and distinguish requested modules from recorded results.");
  if(saved.Id==previous.Id||saved.Vehicle.ProfileId!=profile.ProfileId||Store.Load(Store.SessionPath(previous)).Vehicle.Vin!=previous.Vehicle.Vin)
   throw new Exception("A scan for another VIN modified the previous study.");
  var archive=Path.Combine(Store.Root,"Vehicles",profile.Vin,"AutoScan",scan.Hash+".txt");
  if(!File.ReadAllBytes(archive).SequenceEqual(File.ReadAllBytes(path)))throw new Exception("The original Auto-Scan bytes were not preserved.");
 }

 static void CheckSavedChannelLabels(string root)
 {
  Channel Generic(string address,string name="Voltage")=>new(){Address=address,Name=name,OriginalName="Voltage",Unit="V"};
  var alf=new LogData{Source="VCDS CSV",Ecu="4B0-907-552-F",Channels=[Generic("031.1"),Generic("031.3"),Generic("033.2","Nazwa użytkownika")]};
  var other=new LogData{Source="VCDS CSV",Ecu="4B0-907-552-A",Channels=[Generic("031.1")]};
  string saved=Path.Combine(root,"labels-regression.json");
  Store.WriteAtomic(saved,System.Text.Json.JsonSerializer.Serialize(new Study{Logs=[alf,other]}));
  var loaded=Store.Load(saved);
  if(loaded.Logs[0].Channels[0].Name!="Napięcie sondy B1S1 (rzeczywiste)"||
     loaded.Logs[0].Channels[1].Name!="Napięcie sondy B2S1 (rzeczywiste)"||
     loaded.Logs[0].Channels[2].Name!="Nazwa użytkownika"||loaded.Logs[1].Channels[0].Name!="Voltage")
   throw new Exception("Saved logs did not retain ECU-specific or user-defined labels.");
 }

 async Task CheckVcdsDiscovery(MainWindow window,string root,LogData real,BindingFlags flags)
 {
  if(window.Width>900||window.Height>650)throw new Exception("Main window is larger than the compact defaults.");
  string directory=Path.Combine(root,"VcdsWatchTest");
  Directory.CreateDirectory(directory);
  File.Copy(real.OriginalPath,Path.Combine(directory,"old-log.csv"),true);
  var type=typeof(MainWindow);
  var current=type.GetField("followedLog",flags)!;
  type.GetMethod("StartVcdsDiscovery",flags)!.Invoke(window,[new[]{directory}]);
  await Task.Delay(2250);
  if(current.GetValue(window)!=null)throw new Exception("An existing idle CSV started observation.");

  type.GetMethod("MinimizeApp",flags)!.Invoke(window,[window,new RoutedEventArgs()]);
  if(window.WindowState!=WindowState.Minimized)throw new Exception("Minimize control did not minimize DiagScope.");

  string path=Path.Combine(directory,"new-log.csv");
  var lines=File.ReadAllLines(real.OriginalPath);
  int split=Math.Min(28,lines.Length-3);
  using var stream=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read);
  using var writer=new StreamWriter(stream);
  foreach(var line in lines.Take(split))writer.WriteLine(line);
  writer.Flush();
  await WaitFor(()=>current.GetValue(window) is LogData {Rows: >0},"VCDS CSV was not detected while minimized.");
  var initial=(LogData)current.GetValue(window)!;
  int initialRows=initial.Rows;
  if(initial.OriginalPath!=path||Windows.OfType<LogWindow>().Any())
   throw new Exception("Automatic VCDS plot interrupted the minimized window.");

  foreach(var line in lines.Skip(split))writer.WriteLine(line);
  writer.Flush();
  await WaitFor(()=>initial.Rows>initialRows,"Automatic VCDS observation stopped while minimized.");
  window.WindowState=WindowState.Normal;
  await WaitFor(()=>Windows.OfType<LogWindow>().Count()==1,"The live plot did not appear after restoring DiagScope.");
  var graph=Windows.OfType<LogWindow>().Single();
  if(graph.Width>900||graph.Height>650)throw new Exception("Log window is larger than the compact defaults.");
  window.WindowState=WindowState.Minimized;
  if(graph.WindowState!=WindowState.Minimized)throw new Exception("Plot remained open while DiagScope was minimized.");
  window.WindowState=WindowState.Normal;
  graph.Close();
  type.GetMethod("StopVcdsDiscovery",flags)!.Invoke(window,null);
  type.GetMethod("StopFollow",flags)!.Invoke(window,null);
 }

 static async Task WaitFor(Func<bool> check,string message)
 {
  for(int i=0;i<80;i++){if(check())return;await Task.Delay(100);}
  throw new Exception(message);
 }
}