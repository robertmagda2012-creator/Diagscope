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
    var menu=(WrapPanel)window.FindName("Nav");
    if(!menu.Children.OfType<Button>().Select(button=>button.Tag as string).SequenceEqual(
       new[]{"Pojazdy","Pomiary","Analizator logów","Diagnostyka","Wyniki","Ustawienia"})||
       ((StackPanel)window.FindName("Page")).Children.OfType<TextBlock>().First().Text!="Pojazdy")
     throw new Exception("The Vehicles tab is not first and selected at startup, or the standalone Auto Scan tab is present.");
    if(Environment.GetEnvironmentVariable("DIAGSCOPE_DTC_SMOKE") is { } dtcMode)
    {
     string result = await window.CheckDtcImport(dtcMode == "restart");
     File.WriteAllText(Path.Combine(dir,"ui-result.txt"),result);
     Shutdown(0);
     return;
    }
    if(Environment.GetEnvironmentVariable("DIAGSCOPE_VEHICLE_CARD_SMOKE")=="1")
    {
     await CheckVehicleCardButton(window,study,flags);
     File.WriteAllText(Path.Combine(dir,"ui-result.txt"),"PASS: saved Polo profile opened and brought into view in the existing active visit.");
     Shutdown(0);
     return;
    }
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
    foreach(string page in new[]{"Pojazdy","Pomiary","Diagnostyka","Wyniki","Ustawienia","Analizator logów"}) {
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
    CheckVehicleSaveButton(window,flags);
    window.UpdateLayout();Capture(window,Path.Combine(dir,"ui","Auto-Scan-import.png"));
    CheckVehicleHistory(window,dir,flags);
    window.UpdateLayout();Capture(window,Path.Combine(dir,"ui","Auto-Scan-bledy.png"));
    File.WriteAllText(Path.Combine(dir,"ui-result.txt"),"PASS: Vehicles first, Auto Scan and DTC inside Vehicles, extended report metadata, save button and complete vehicle label, preserved visits, Polish DTC, LPG opt-in and live CSV.");
    Shutdown(0);
   }catch(Exception ex){File.WriteAllText(Path.Combine(dir,"ui-error.txt"),ex.ToString());Shutdown(2);}
  }));
 }

 static void CheckVehicleSaveButton(MainWindow window,BindingFlags flags)
 {
  var type=typeof(MainWindow);
  var page=(StackPanel)window.FindName("Page");
  if(!page.Children.OfType<WrapPanel>().SelectMany(row=>row.Children.OfType<Button>()).Any(button=>(string)button.Content=="Wczytaj Auto Scan")||
     page.Children.OfType<Border>().Select(border=>border.Child).OfType<Expander>().All(section=>!((string)section.Header).StartsWith("Błędy DTC z ostatniego badania")))
   throw new Exception("The Vehicles screen does not expose the import and saved readings.");
  var details=(Expander)type.GetField("vehicleCardDetails",flags)!.GetValue(window)!;
  var fields=((StackPanel)details.Content).Children;
  TextBox Field(string label)=>(TextBox)fields[fields.IndexOf(fields.OfType<TextBlock>().Single(block=>block.Text==label))+1];
  Field("Pojemność silnika [cm³ lub l]").Text="1390";
  Field("Data lub rok produkcji").Text="03.02.2006";
  fields.OfType<Button>().Single(button=>(string)button.Content=="Zapisz pojazd").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var vehicle=Store.Startup().Vehicle;
  var saved=VehicleProfiles.Read().Single(item=>item.Vehicle.ProfileId==vehicle.ProfileId);
  if(saved.Label!=$"VW Polo · 1390 cm³ · 03.02.2006 · {vehicle.Vin}"||saved.Vehicle.ProductionDate!=new DateTime(2006,2,3))
   throw new Exception("The Save vehicle button did not persist the label fields.");
  if(new VehicleProfile(new Vehicle{Make="VW",Model="Polo"}).Label!="VW Polo")
   throw new Exception("The vehicle label invented missing data.");
 }

 static async Task CheckVehicleCardButton(MainWindow window,Study study,BindingFlags flags)
 {
  var profile=VehicleProfiles.Read().Single(item=>item.Vehicle.ProfileId==study.Vehicle.ProfileId);
  if(!profile.Vehicle.Model.Contains("Polo",StringComparison.OrdinalIgnoreCase)||profile.Vehicle.Vin!=study.Vehicle.Vin)
   throw new Exception("The saved Polo card does not match the active visit.");
  var visits=DiagScope2.MainWindow.ReadStudies(profile.Vehicle);
  if(visits.FirstOrDefault()?.Id!=study.Id)throw new Exception("The latest Polo visit is not active.");

  var type=typeof(MainWindow);
  type.GetMethod("Navigate",flags)!.Invoke(window,["Pojazdy"]);
  var page=(StackPanel)window.FindName("Page");
  var listPanel=(StackPanel)((Border)page.Children[1]).Child;
  var search=listPanel.Children.OfType<TextBox>().Single();
  var matches=(StackPanel)listPanel.Children.OfType<ScrollViewer>().Single().Content;
  if(type.GetField("vehicleCardDetails",flags)!.GetValue(window) is not null)
   throw new Exception("The vehicle list did not open first.");
  foreach(var term in new[]{"VW","polo",profile.Vehicle.Vin.ToLowerInvariant()})
  {
   search.Text=term;
   if(!matches.Children.OfType<Button>().Any(button=>((TextBlock)button.Content).Text==profile.Label))
    throw new Exception("Vehicle search did not find Polo by make, model or VIN.");
  }
  search.Text="NO-VEHICLE-MATCH";
  if(matches.Children.OfType<Button>().Any())throw new Exception("Vehicle search did not filter the list.");
  search.Text=profile.Vehicle.Vin;
  var open=matches.Children.OfType<Button>().Single(button=>((TextBlock)button.Content).Text==profile.Label);
  open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var details=(Expander)type.GetField("vehicleCardDetails",flags)!.GetValue(window)!;
  window.UpdateLayout();
  if(!((StackPanel)details.Content).Children.OfType<TextBox>().Any(box=>box.Text==profile.Vehicle.Vin)||
     page.Children.OfType<Border>().Select(border=>border.Child).OfType<StackPanel>()
      .All(panel=>panel.Children.OfType<TextBlock>().All(block=>block.Text!="Historia badań")))
   throw new Exception("Click did not show the saved Polo details and study history.");
  var identity=(Border)type.GetField("vehicleIdentity",flags)!.GetValue(window)!;
  DependencyObject? parent=identity;
  while(parent is not null&&parent is not ScrollViewer)
   parent=System.Windows.Media.VisualTreeHelper.GetParent(parent);
  if(parent is not ScrollViewer scroll)throw new Exception("The vehicle card scroll area was not found.");
  await WaitFor(()=>{
   double top=identity.TranslatePoint(new Point(0,0),scroll).Y;
   return top>=-1&&top+24<=scroll.ViewportHeight+1;
  },"The card header did not scroll into view.");
  if(Store.Startup().Id!=study.Id||DiagScope2.MainWindow.ReadStudies(profile.Vehicle).Count!=visits.Count)
   throw new Exception("Opening the active card changed the visit or created a duplicate.");
 }

 static void CheckVehicleHistory(MainWindow window,string root,BindingFlags flags)
 {
  var type=typeof(MainWindow);
  var previous=Store.Startup();
  var oldSnapshot=File.ReadAllBytes(Store.SessionPath(previous));
  var vehicle=previous.Vehicle;
  vehicle.EngineType="1.4 FSI";
  vehicle.PaintCode="TEST";
  VehicleProfiles.Save(vehicle);
  type.GetMethod("BeginStudy",flags)!.Invoke(window,[vehicle]);
  var next=Store.Startup();
  if(next.Id==previous.Id||next.Vehicle.Vin!=vehicle.Vin||next.AutoScans.Count!=0)
   throw new Exception("A new visit required a new scan or lost its vehicle association.");
  string partial=Path.Combine(root,"one-module.txt");
  File.WriteAllText(partial,"""
    Sunday,01,November,2026,10:00:00:16365
    VCDS Version: 23.3.1.0
    Address 03: ABS Brakes
    Control Module Part Number: 6Q0 907 379 AF
    Software Coding: 0002170
    Software Version: 0002
    No fault code found.
    Monday,02,November,2026,10:00:00:16365
    VCDS Version: 23.3.1.0
    Address 03: ABS Brakes
    Control Module Part Number: 6Q0 907 379 AF
    Software Coding: 0002171
    No fault code found.
    """);
  var assignedImport=type.GetMethod("ImportReportForVehicle",flags)!;
  assignedImport.Invoke(window,[partial,vehicle.Vin]);
  assignedImport.Invoke(window,[partial,vehicle.Vin]);
  var saved=Store.Startup();
  if(saved.Id!=next.Id||saved.AutoScans.Count!=2||saved.Vehicle.ScanModules.Count!=5||
     saved.Vehicle.ModuleReadings.Count(record=>record.Module.Address=="03")!=3||
     saved.Vehicle.ScanModules.Single(module=>module.Address=="03").Coding!="0002171"||
     saved.Vehicle.ModuleReadings.Single(record=>record.Module.Coding=="0002170").Module.SoftwareVersion!="0002"||
     saved.Vehicle.EngineType!="1.4 FSI"||saved.Vehicle.PaintCode!="TEST"||saved.Vehicle.LastScanMileageKm!=200490||
     saved.Vehicle.LastMileageAt!=new DateTime(2026,10,25,20,59,33)||DiagScope2.MainWindow.ReadStudies(vehicle).Count!=2)
   throw new Exception("A partial report changed the visit, discarded previous modules, or removed manual fields.");

  if(!File.ReadAllBytes(Store.SessionPath(previous)).SequenceEqual(oldSnapshot))
   throw new Exception("Starting or importing into a new study rewrote an earlier study.");
  CheckStudyAssociation(window,flags,vehicle);
  string? audiPath=Environment.GetEnvironmentVariable("DIAGSCOPE_AUTOSCAN_AUDI_PATH");
  if(audiPath is not null)
  {
   var audi=VehicleReport.ReadAll(Importer.ReadText(audiPath)).Single();
   if(audi.Vehicle.Vin.Length!=0||audi.Vehicle.Capacity!="2,4 l"||audi.Vehicle.ProductionDate.HasValue||audi.Chassis!="4B"||audi.RequestedAddresses.Count!=23||audi.Modules.Count!=5||
      audi.Modules.Single(module=>module.Address=="01").Readiness!="N/A"||
      audi.Modules.Single(module=>module.Address=="17").Software!="4B0 919 860 F"||
      audi.Modules.Single(module=>module.Address=="17").SubsystemDetails.Count==0||
      audi.Faults.Count!=3||!audi.Faults.Any(fault=>fault.Code.Contains("P0130")))
    throw new Exception("The actual Audi report lost chassis, continued addresses, modules or faults.");
   string? combinedPath=Environment.GetEnvironmentVariable("DIAGSCOPE_AUTOSCAN_COMBINED_PATH");
   if(combinedPath is not null)
   {
    var reports=VehicleReport.ReadAll(Importer.ReadText(combinedPath));
    if(reports.Count!=2||reports[0].Faults.Count!=3||reports[1].Faults.Count!=5||reports[0].ScannedAt>=reports[1].ScannedAt)
     throw new Exception("Appended VCDS readings were mixed with earlier faults or measurement-only blocks.");
   }
   var audiVehicle=new Vehicle{Vin="WAUZZZ4BZWN123456",Make="Audi",Model="A6 C5"};
   VehicleProfiles.Save(audiVehicle);
   assignedImport.Invoke(window,[audiPath,audiVehicle.Vin]);
   var audiStudy=Store.Startup();
   if(audiStudy.Vehicle.Vin!=audiVehicle.Vin||audiStudy.AutoScans.Count!=1||audiStudy.Dtcs.Count!=3)
    throw new Exception("VIN-less Audi report was not associated with the selected profile.");
  }
  var page=(StackPanel)window.FindName("Page");
  if(page.FindResource("SurfaceText") is not System.Windows.Media.SolidColorBrush ink ||
     ink.Color!=System.Windows.Media.Color.FromRgb(21,43,67) ||
     window.FindResource("SurfaceText") is not System.Windows.Media.SolidColorBrush chrome || chrome.Color!=System.Windows.Media.Colors.White)
   throw new Exception("The workspace and original header color scopes were not kept separate.");
 }

 static void CheckStudyAssociation(MainWindow window,BindingFlags flags,Vehicle vehicle)
 {
  var type=typeof(MainWindow);
  var active=Store.Startup();
  var legacy=new Study
  {
   Date=active.Date.AddDays(-1),
   Vehicle=new Vehicle{Vin=vehicle.Vin,Make=vehicle.Make,Model=vehicle.Model},
   Visit=new(200000,"Zgłoszenie testowe","Odczyt","Sprawdzić"),
   Entries=[new Entry{Title="Wynik analizy",Note="Zachowany wynik"}],
   Logs=[new LogData{Name="zapis.csv"}]
  };
  var unrelated=new Study{Vehicle=new Vehicle{Make=vehicle.Make,Model=vehicle.Model}};
  Store.WriteAtomic(Store.SessionPath(legacy),System.Text.Json.JsonSerializer.Serialize(legacy,Store.Options));
  Store.WriteAtomic(Store.SessionPath(unrelated),System.Text.Json.JsonSerializer.Serialize(unrelated,Store.Options));
  var history=DiagScope2.MainWindow.ReadStudies(vehicle);
  var linked=history.Single(item=>item.Id==legacy.Id);
  if(history[0].Id!=active.Id||history.Any(item=>item.Id==unrelated.Id)||
     linked.Visit?.Concern!="Zgłoszenie testowe"||linked.Entries.Single().Note!="Zachowany wynik"||linked.Logs.Single().Name!="zapis.csv")
   throw new Exception("Study history missed a legacy study or matched an unrelated car by model.");
  var ambiguous=VehicleReport.Read("VIN: WAUZZZ4BZWN123456\nVIN: WVWZZZ9NZ6Y123456\nAddress 01: Engine\nNo fault code found.");
  if(!ambiguous.AmbiguousVin||ambiguous.Vehicle.Vin.Length>0||!ambiguous.Recognized)
   throw new Exception("Conflicting VINs must use manual assignment.");
  var production=VehicleReport.Read("VCDS Version: 23.3.1.0\nVIN: WVWZZZ9NZ6Y123456\nRok produkcji: 2006\nWyposażenie: test");
  if(production.Vehicle.ProductionYear!=2006||production.Vehicle.ProductionDate.HasValue||production.Vehicle.Equipment!="test")
   throw new Exception("Year-only vehicle metadata was lost or an exact date was invented.");

  var cardOnly=new Vehicle{Make="VW",Model="Polo",Equipment="Inna karta"};
  VehicleProfiles.Save(cardOnly);
  type.GetMethod("OpenVehicle",flags)!.Invoke(window,[cardOnly]);
  if(DiagScope2.MainWindow.ReadStudies(cardOnly).Count!=0||Store.Startup().Id!=active.Id)
   throw new Exception("Opening a card without studies created a study or matched another car.");
  type.GetMethod("OpenVehicleStudy",flags)!.Invoke(window,[active.Id,vehicle,"Pojazdy"]);
  if(Store.Startup().Id!=active.Id)
   throw new Exception("Could not reopen the earlier active study.");
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
  Store.Save(previous);
  current.SetValue(window,previous);
  type.GetField("studyIsDraft",flags)!.SetValue(window,false);
  var import=type.GetMethod("ImportReport",flags)!;
  import.Invoke(window,[path]);
  import.Invoke(window,[path]);
  var saved=Store.Startup();
  var scan=saved.AutoScans.Single();
  var profile=VehicleProfiles.Read().Single().Vehicle;
  var report=scan.Report;
  var abs=report.Modules.Single(m=>m.Address=="03");
  if(realPath is not null&&(report.DataVersion.Length==0||abs.Vcid.Length==0||abs.WorkshopCode.Length==0))
   throw new Exception("Additional fields from the real Polo Auto Scan were not imported.");
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