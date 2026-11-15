using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
namespace DiagScope2;
public partial class MainWindow:Window {
 Study study=new();string page="Pojazdy";Dictionary<string,LogWindow> windows=[];DispatcherTimer? followTimer;string followPath="";
 public static string[] Statuses=["DO SPRAWDZENIA","HIPOTEZA","POTWIERDZONE"];
 public MainWindow(){InitializeComponent();try{study=Store.Startup();studyIsDraft=!File.Exists(Store.SessionPath(study));}catch(Exception e){MessageBox.Show("Nie udało się odczytać ostatniego badania. Oryginał i kopia .bak pozostają na dysku.\n"+e.Message);}
 Navigate(page);
 StateChanged+=OnMainWindowStateChanged;
 Closing+=(s,e)=>{if(!Persist()&&MessageBox.Show("Zapis się nie udał. Zamknąć mimo to?","DiagScope",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)e.Cancel=true;};
 Closed+=(s,e)=>{analysisCancel?.Cancel();StopVcdsDiscovery();StopFollow();foreach(var w in windows.Values.ToArray())w.Close();};
 LoadVcdsAutoFollowSetting();
 }
 public static Brush B(string c)=>(Brush)new BrushConverter().ConvertFrom(c)!;
 public static TextBlock Txt(string t,int size=16)=>new(){Text=t,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
 public static Button Btn(string text,Action action){var b=new Button{Content=text};b.Click+=(s,e)=>{try{action();}catch(Exception ex){MessageBox.Show(ex.Message,"DiagScope — operacja nie została ukończona");}};return b;}
 public static Border Card(UIElement body)
 {
  var border=new Border{Child=body,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(16),Margin=new Thickness(0,0,0,14)};
  border.SetResourceReference(Border.BackgroundProperty,"SurfaceCard");
  border.SetResourceReference(Border.BorderBrushProperty,"SurfaceBorder");
  return border;
 }
 public static StackPanel Stack(params UIElement[] children){var p=new StackPanel();foreach(var c in children)p.Children.Add(c);return p;}
 public static WrapPanel Row(params UIElement[] children){var p=new WrapPanel();foreach(var c in children)p.Children.Add(c);return p;}
 bool Persist(){try{if(studyIsDraft&&!HasStudyData(study))return true;Store.Save(study);studyIsDraft=false;StorageState.Text="Zapisano lokalnie · "+DateTime.Now.ToString("HH:mm:ss");return true;}catch(Exception e){StorageState.Text="BŁĄD ZAPISU — dane tylko w pamięci";MessageBox.Show("Nie udało się zapisać badania. Wyeksportuj JSON w inne miejsce przed zamknięciem.\n"+e.Message,"Błąd zapisu");return false;}}
 void OnNavigationClick(object sender, RoutedEventArgs e) {
  if (sender is not Button { Tag: string destination }) return;
  try { Navigate(destination); }
  catch (Exception ex) { MessageBox.Show(ex.Message, "DiagScope - operacja nie została ukończona"); }
 }
 void Navigate(string name){followStatus=null;vehicleCardDetails=null;vehicleScans=null;vehicleIdentity=null;page=name;foreach(var b in Nav.Children.OfType<Button>()){bool active=Equals(b.Tag,name);b.BorderBrush=B(active?"#74BEFF":"#4B6582");b.BorderThickness=new Thickness(active?2:1);b.Background=B(active?"#183B60":"#15283F");}Page.Children.Clear();switch(name){case "Diagnostyka":Home();break;case "Pomiary":Measurements();break;case "Analizator logów":AnalyzerPage();break;case "Pojazdy":Vehicles();break;case "Wyniki":Results();break;default:Settings();break;}Footer.Text=$"Badanie: {study.Date:dd.MM.yyyy HH:mm} · {study.Vehicle.Make} {study.Vehicle.Model} · dane lokalne";}
 void Heading(string h,string p=""){Page.Children.Add(Txt(h,30));if(p.Length>0)Page.Children.Add(Txt(p));}
 void Home()
 {
  Heading("Diagnostyka");
  if(study.Vehicle.HasLpg==true)
  {
   var section=Stack(Txt("Instalacja LPG",21));
   if(!string.IsNullOrWhiteSpace(study.Vehicle.Lpg))section.Children.Add(Txt("Sterownik LPG: "+study.Vehicle.Lpg,14));
   section.Children.Add(Row(Btn("Wczytaj log sterownika LPG",()=>ImportLog()),Btn("CSV LPG — wybierz kolumny",()=>ImportLog(true)),
      Btn("Porównaj benzynę i LPG",()=>{Navigate("Analizator logów");if(Page.Children.OfType<Expander>().FirstOrDefault() is { } comparison)comparison.IsExpanded=true;})));
   Page.Children.Add(Card(section));
  }

  var next=Stack(Txt("Co sprawdzić dalej",21));
  if(study.Dtcs.Count>0)
  {
   next.Children.Add(Txt("Otwórz odczyt błędu i zapisz wynik następnego testu."));
   next.Children.Add(Btn("Odczyty i błędy DTC",()=>{ShowVehicleCard();RevealVehicleSection(vehicleScans);}));
  }
  if(study.Logs.Count>0)next.Children.Add(Btn("Przejdź do analizy logów",()=>Navigate("Analizator logów")));
  if(study.Dtcs.Count==0&&study.Logs.Count==0)next.Children.Add(Btn("Wczytaj raport lub log",()=>ImportVehicleFile()));
  next.Children.Add(Btn("Zapisz obserwację",AddObservation));
  Page.Children.Add(Card(next));
 }
 void AddObservation(){var d=new FormDialog(this,"Nowa obserwacja");var title=d.Field("Nazwa");var src=d.Field("Źródło");var note=d.Field("Wynik / dowód",multiline:true);var status=d.Select("Status",Statuses);d.Accept=()=>{if(string.IsNullOrWhiteSpace(title.Text)||string.IsNullOrWhiteSpace(note.Text))throw new Exception("Podaj nazwę i wynik.");study.Entries.Add(new(){Title=title.Text,Source=src.Text,Note=note.Text,Status=status.Text});Persist();};if(d.ShowDialog()==true)Navigate(page);}
 void Vehicles()
 {
  Heading("Pojazdy");
  var saved = VehicleProfiles.Read();
  Page.Children.Add(SavedVehicles(saved));
  Page.Children.Add(Row(Btn("Dodaj pojazd",NewVehicle),Btn("Wczytaj Auto Scan",PickAutoScan)));
  if(!showVehicleCard&&study.Vehicle.ProfileId.Length==0&&HasStudyData(study))
   Page.Children.Add(Btn("Otwórz bieżące badanie bez karty",()=>ShowVehicleCard()));
  if(!showVehicleCard) return;

  var vehicleProfile=saved.FirstOrDefault(profile=>profile.Vehicle.ProfileId==study.Vehicle.ProfileId);
  var vehicle=vehicleProfile?.Vehicle??study.Vehicle;
  var identity=Stack(Txt(new VehicleProfile(vehicle).Label,23));
  AddScanField(identity,"Silnik",string.Join(" · ",new[]{vehicle.EngineType,vehicle.Engine}.Where(value=>value.Length>0)));
  AddScanField(identity,"Wyposażenie",vehicle.Equipment);
  if(vehicle.HasLpg==true)AddScanField(identity,"LPG",vehicle.Lpg.Length>0?vehicle.Lpg:"Tak");
  AddScanField(identity,"Typ nadwozia",vehicle.Chassis);
  AddScanField(identity,"Ostatni skan",vehicle.LastScanAt?.ToString("dd.MM.yyyy HH:mm:ss")??"");
  AddScanField(identity,"Przebieg ze skanu",vehicle.LastScanMileageKm is int km?$"{km:N0} km · {ScanDate(vehicle.LastMileageAt??vehicle.LastScanAt)}":"");
  if(vehicle.ScanModules.Count>0)identity.Children.Add(VehicleModuleDetails(vehicle));
  var vehicleActions=Row(Btn("Nowe badanie",()=>BeginStudy(study.Vehicle.ProfileId.Length>0?vehicle:throw new InvalidOperationException("Najpierw zapisz kartę pojazdu."))),
    Btn("Wczytaj Auto Scan",PickAutoScan));
  if(vehicleProfile is not null)vehicleActions.Children.Add(Btn("Importuj błędy VCDS",()=>ImportVcdsFaults(vehicle)));
  identity.Children.Add(vehicleActions);
  vehicleIdentity=Card(identity);
  Page.Children.Add(vehicleIdentity);

  var panel=new StackPanel();
  var fields=new Dictionary<string,TextBox>();
  TextBox Field(string key,string label,string value)
  {
   var input=new TextBox{Text=value};
   fields[key]=input;
   panel.Children.Add(Txt(label,14));
   panel.Children.Add(input);
   return input;
  }
  Field("make","Marka",vehicle.Make);
  Field("model","Model",vehicle.Model);
  Field("vin","VIN",vehicle.Vin);
  Field("engineType","Typ silnika",vehicle.EngineType);
  Field("engine","Kod silnika",vehicle.Engine);
  Field("production","Data lub rok produkcji",VehicleProfile.ProductionLabel(vehicle));
  Field("equipment","Wyposażenie",vehicle.Equipment);
  Field("gearbox","Kod skrzyni",vehicle.GearboxCode);
  Field("paint","Kod lakieru",vehicle.PaintCode);
  Field("registration","Numer rejestracyjny",vehicle.Registration);
  Field("fuel","Rodzaj paliwa",vehicle.Fuel);
  Field("capacity","Pojemność silnika [cm³ lub l]",vehicle.Capacity);
  var lpg=new ComboBox{ItemsSource=new[]{"Nie wybrano","Tak","Nie"},SelectedIndex=vehicle.HasLpg switch{true=>1,false=>2,_=>0}};
  panel.Children.Add(Txt("Czy auto ma LPG?",14));
  panel.Children.Add(lpg);
  var controller=Field("lpg","Sterownik LPG",vehicle.Lpg);
  controller.IsEnabled=lpg.SelectedIndex==1;
  lpg.SelectionChanged+=(s,e)=>controller.IsEnabled=lpg.SelectedIndex==1;
  Field("source","Źródło danych",vehicle.Source);
  panel.Children.Add(Btn("Zapisz pojazd",()=>{
   string vin=LogMetadata.ValidVin(fields["vin"].Text);
   if(vin.Length==0&&fields["vin"].Text.Trim().Length>0)throw new FormatException("VIN musi mieć 17 znaków.");
   if(vehicle.Vin.Length>0&&vin!=vehicle.Vin&&
      (vehicle.ProfileId.Length>0||HasStudyData(study)))throw new InvalidOperationException("Aby zapisać inne auto, rozpocznij nowe badanie.");
   var updated=vehicle.Copy();
   string production=fields["production"].Text.Trim();
   if(production.Length>0)
   {
    if(DateTime.TryParseExact(production,["d.M.yyyy","yyyy-MM-dd"],System.Globalization.CultureInfo.InvariantCulture,
      System.Globalization.DateTimeStyles.None,out var date))
    {
     updated.ProductionDate=date;
     updated.ProductionYear=null;
    }
    else if(Vehicle.ParseProductionYear(production) is int year)
    {
     if(updated.ProductionDate?.Year!=year)updated.ProductionDate=null;
     updated.ProductionYear=year;
    }
    else throw new FormatException("Wpisz pełną datę produkcji albo sam rok.");
   }
   string Keep(string field,string previous)
   {
    string value=fields[field].Text.Trim();
    return value.Length>0?value:previous;
   }
   updated.Make=Keep("make",updated.Make);
   updated.Model=Keep("model",updated.Model);
   updated.Vin=vin;
   updated.EngineType=Keep("engineType",updated.EngineType);
   updated.Engine=Keep("engine",updated.Engine);
   updated.GearboxCode=Keep("gearbox",updated.GearboxCode);
   updated.PaintCode=Keep("paint",updated.PaintCode);
   updated.Registration=Keep("registration",updated.Registration);
   updated.Fuel=Keep("fuel",updated.Fuel);
   updated.Capacity=Keep("capacity",updated.Capacity);
   updated.Equipment=Keep("equipment",updated.Equipment);
   updated.HasLpg=lpg.SelectedIndex switch{1=>true,2=>false,_=>updated.HasLpg};
   updated.Lpg=Keep("lpg",updated.Lpg);
   updated.Source=Keep("source",updated.Source);
   if(updated.Vin.Length==0&&updated.Make.Length==0&&updated.Model.Length==0)
    throw new FormatException("Podaj VIN, markę lub model pojazdu.");
   VehicleProfiles.Save(updated);
   study.Vehicle=updated;
   if(Persist())
   {
    ShowVehicleCard(expandDetails:true);
    StorageState.Text="Zapisano pojazd";
   }
  }));
  vehicleCardDetails=new Expander{Header="Dane pojazdu",Content=panel,IsExpanded=vehicle.ProfileId.Length==0};
  Page.Children.Add(Card(vehicleCardDetails));
  var history=ReadStudies(vehicle,saved);
  var latest=history.FirstOrDefault();
  if(latest is not null)
   AddVehicleScans(latest.Id==study.Id?study:Store.Load(Store.SessionPath(latest.Id)),vehicle);
  else
   Page.Children.Add(Card(Stack(Txt("Błędy DTC z ostatniego badania",20),Txt("Brak zapisanego badania.",14))));
  if(latest?.Id!=study.Id&&(study.AutoScans.Count>0||study.Dtcs.Count>0))
   AddVehicleScans(study,vehicle,false);
  Page.Children.Add(StudyHistory(vehicle,history));
 }

 static bool HasStudyData(Study item) =>
  item.Entries.Count>0||item.Logs.Count>0||item.Dtcs.Count>0||item.AutoScans.Count>0||
  item.Checks.Count>0||item.Isolation.Count>0||item.Visit!=new VisitNotes();

 void NewVehicle()
 {
  if(!Persist())return;
  SetActiveStudy(new Study());
  studyIsDraft=true;
  ShowVehicleCard(expandDetails:true);
 }
 void ImportVehicleFile()
 {
  var picker=new OpenFileDialog{
   Filter="Raport lub log|*.txt;*.log;*.csv;*.tsv;*.jsonl;*.json|Raport tekstowy|*.txt;*.log|Log pomiarów|*.csv;*.tsv;*.jsonl;*.json",
   InitialDirectory=VcdsLogsDirectory()
  };
  if(picker.ShowDialog()!=true)return;
  if(Path.GetExtension(picker.FileName).ToLowerInvariant() is ".txt" or ".log")ImportReport(picker.FileName);
  else ImportLog(paths:[picker.FileName]);
 }

 void ApplyVehicleVin(string vin,string path)
 {
  if(vin.Length==0)return;
  if(study.Vehicle.Vin.Length>0&&study.Vehicle.Vin!=vin)
   throw new InvalidOperationException("Plik ma VIN innego pojazdu. Rozpocznij nowe badanie dla tego auta.");
  if(study.Vehicle.Vin.Length==0)
  {
   var known=VehicleProfiles.Read().FirstOrDefault(x=>x.Vehicle.Vin==vin);
   if(known is not null&&study.Vehicle.ProfileId.Length==0)study.Vehicle=known.Vehicle;
   study.Vehicle.Vin=vin;
   study.Vehicle.Source=path;
  }
 }

 static string VcdsLogsDirectory()
 {
  const string standard=@"C:\Ross-Tech\VCDS\Logs";
  return Directory.Exists(standard)?standard:Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
 }
 void AddDtcTest(Entry fault)
 {
  var dialog=new FormDialog(this,"Co sprawdzić dalej · "+fault.Title);
  var test=dialog.Field("Test");
  var result=dialog.Field("Wynik / obserwacja",multiline:true);
  var state=dialog.Select("Status",Statuses);
  dialog.Accept=()=>{
   if(string.IsNullOrWhiteSpace(test.Text))throw new ArgumentException("Wpisz nazwę testu.");
   study.Entries.Add(new(){Title=test.Text.Trim(),Source=fault.Title+" · "+fault.Source,Note=result.Text.Trim(),Status=state.Text});
   if(!Persist())throw new IOException("Zapis wyniku nie powiódł się.");
  };
  dialog.ShowDialog();
 }
 async void ImportLog(bool mapped=false,string[]? paths=null){if(paths is null){var pick=new OpenFileDialog{Filter="Logi pomiarów|*.csv;*.tsv;*.txt;*.jsonl;*.json|Wszystkie pliki|*.*",Multiselect=!mapped,InitialDirectory=VcdsLogsDirectory()};if(pick.ShowDialog()!=true)return;paths=pick.FileNames;}var target=study;foreach(string path in paths){try{LogData log;if(mapped){var m=new MappingDialog(this,path);if(m.ShowDialog()!=true)continue;log=m.Result!;}else{try{StorageState.Text="Wczytywanie: "+Path.GetFileName(path);log=await Task.Run(()=>Importer.Read(path));}catch(FormatException){var m=new MappingDialog(this,path);if(m.ShowDialog()!=true)continue;log=m.Result!;}}if(!ReferenceEquals(study,target))return;if(study.Logs.Any(l=>l.Hash==log.Hash)){OpenLog(study.Logs.First(l=>l.Hash==log.Hash));continue;}ApplyVehicleVin(log.Vin,path);Store.PreserveSource(log,study);study.Logs.Add(log);study.Entries.Add(new(){Title="Import logu",Source=log.Name,Status=Statuses[2],Note=$"Kanały: {log.Channels.Count}; wiersze: {log.Rows}. Oryginał zachowany. Poprawność mapowania wymaga weryfikacji."});Persist();OpenLog(log);}catch(Exception e){MessageBox.Show($"Nie wczytano {path}\n{e.Message}");}}Navigate(page);}
 void Measurements()
 {
  Heading("Pomiary");
  var auto=new CheckBox{Content="Automatycznie wykrywaj logi VCDS",IsChecked=vcdsDiscoveryTimer!=null};
  auto.Checked+=(s,e)=>ChangeVcdsAutoFollow(auto,true);
  auto.Unchecked+=(s,e)=>ChangeVcdsAutoFollow(auto,false);
  followStatus=Txt("",14);
  saveFollowed=Btn("Zapisz obserwowany log",()=>{
   if(followedLog!=null)ImportLog(paths:[followPath]);
  });
  var showLive=Btn("Pokaż wykres na żywo",()=>{
   if(followedLog!=null)OpenLogWindow(followedLog);
  });
  showFollowed=showLive;
  Page.Children.Add(Card(Stack(auto,followStatus,Row(
   Btn("Obserwuj plik zapisywany przez VCDS",StartFollow),
   Btn("Zatrzymaj obserwację",StopFollowFromMeasurements),
   showLive,saveFollowed))));
  UpdateFollowStatus();
  Page.Children.Add(Row(Btn("Wczytaj log",()=>ImportLog()),
   Btn("CSV — wybierz kolumny",()=>ImportLog(true)),
   Btn("Folder logów VCDS",()=>Process.Start(new ProcessStartInfo(VcdsLogsDirectory()){UseShellExecute=true}))));
  foreach(var log in study.Logs.OrderByDescending(LogMetadata.LocalTime))
  {
   var captured=log;
   Page.Children.Add(Card(Stack(Txt(log.Name,22),
    Txt($"{LogMetadata.LocalTime(log)?.ToString("dd.MM.yyyy HH:mm:ss")??"Brak daty pomiaru"} · {log.Source} · {log.Channels.Count} kanałów · {log.Rows} wierszy",14),
    Row(Btn("Otwórz wykres",()=>OpenLog(captured)),Btn("Analiza ciągłości",()=>ShowAnalysis(captured)),Btn("Eksport logu JSON",()=>ExportLog(captured))),
    Txt(string.Join("\n",log.Warnings.Take(5)),13))));
  }
 }
 void OpenLog(LogData log)=>OpenLogWindow(log).ShowFull();
 LogWindow OpenLogWindow(LogData log,bool activate=true)
 {
  if(windows.TryGetValue(log.Id,out var existing)){
   if(activate){if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;existing.Activate();}
   return existing;
  }
  var window=new LogWindow(log,()=>Persist(),Resources,study.Vehicle.HasLpg==true){ShowActivated=activate};
  windows[log.Id]=window;
  window.Closed+=(s,e)=>{windows.Remove(log.Id);minimizedGraphs.Remove(window);};
  window.Show();
  return window;
 }
 void ShowAnalysis(LogData l){var d=new FormDialog(this,"Ciągłość: "+l.Name);var text=d.Field("Wynik obliczeń",Analyzer.Summary(l),true);text.Height=400;text.IsReadOnly=true;d.Accept=()=>{study.Entries.Add(new(){Title="Analiza ciągłości: "+l.Name,Source=l.Source,Status=Statuses[2],Note=text.Text});Persist();};d.ShowDialog();}
 void ExportLog(LogData l){var p=new SaveFileDialog{Filter="Log JSON|*.json",FileName=LogMetadata.FileStem(l)+"-DiagScope.json"};if(p.ShowDialog()==true)Store.WriteAtomic(p.FileName,JsonSerializer.Serialize(l,Store.Options));}
 void Results(){Heading("Wyniki",$"Badanie z {study.Date:dd.MM.yyyy HH:mm:ss}. Wyniki zapisane lokalnie.");Page.Children.Add(Row(Btn("Dodaj obserwację",AddObservation),Btn("Eksport badania JSON",ExportStudy),Btn("Eksport wyników CSV",ExportCsv),Btn("Raport HTML / druk / PDF",Report),Btn("Otwórz wcześniejsze badanie",OpenStudy)));foreach(var e in study.Entries.AsEnumerable().Reverse())Page.Children.Add(Card(Stack(Txt(e.Title,21),Txt($"{e.Date:dd.MM.yyyy HH:mm:ss} · {e.Status} · {e.Outcome}",13),Txt(e.Source,14),Txt(e.Note))));}
 void ExportStudy(){var p=new SaveFileDialog{Filter="Badanie DiagScope|*.json",FileName=$"DiagScope-{study.Date:yyyyMMdd-HHmm}.json"};if(p.ShowDialog()==true)Store.WriteAtomic(p.FileName,JsonSerializer.Serialize(study,Store.Options));}
 void OpenStudy(){Directory.CreateDirectory(Path.Combine(Store.Root,"Sessions"));var p=new OpenFileDialog{Filter="Badanie DiagScope|*.json",InitialDirectory=Path.Combine(Store.Root,"Sessions")};if(p.ShowDialog()!=true)return;var loaded=Store.Load(p.FileName);if(!Persist())return;SetActiveStudy(loaded);if(Persist())Navigate(page);}
 void ExportCsv(){var p=new SaveFileDialog{Filter="Wyniki CSV|*.csv",FileName="DiagScope-wyniki.csv"};if(p.ShowDialog()!=true)return;string Quote(string s)=>"\""+(Regex.IsMatch(s,"^[=+@-]")?"'":"")+s.Replace("\"","\"\"")+"\"";Store.WriteAtomic(p.FileName,"\uFEFFData;Test;Status;Wynik;Źródło;Obserwacja\n"+string.Join("\n",study.Entries.Select(e=>string.Join(";",new[]{e.Date.ToString("o"),e.Title,e.Status,e.Outcome,e.Source,e.Note}.Select(Quote)))));}
 void Report(){var p=new SaveFileDialog{Filter="Raport HTML|*.html",FileName=$"DiagScope-raport-{study.Date:yyyyMMdd-HHmm}.html"};if(p.ShowDialog()!=true)return;string E(string s)=>WebUtility.HtmlEncode(s);var h=new StringBuilder("<!doctype html><html lang='pl'><meta charset='utf-8'><title>DiagScope — raport</title><style>body{font:16px Segoe UI,Arial;margin:40px;color:#15283f}h1{color:#2266ac}section{border:1px solid #bbc;padding:18px;margin:16px 0;break-inside:avoid}pre{white-space:pre-wrap}button{padding:12px}@media print{button{display:none}}</style><button onclick='print()'>Drukuj / Zapisz jako PDF</button><h1>DiagScope 2.0 — raport badania</h1>");h.Append($"<p>{study.Date:dd.MM.yyyy HH:mm:ss zzz}</p><section><h2>Pojazd</h2><p>{E(study.Vehicle.Make)} {E(study.Vehicle.Model)} · VIN {E(study.Vehicle.Vin)} · {E(study.Vehicle.Engine)} · {E(study.Vehicle.Capacity)} cm³ · {E(study.Vehicle.Lpg)}</p><p>{E(study.Vehicle.Status)} · źródło: {E(study.Vehicle.Source)}</p></section>");foreach(var e in study.Dtcs.Concat(study.Entries))h.Append($"<section><h2>{E(e.Title)}</h2><p>{e.Date:dd.MM.yyyy HH:mm:ss} · {E(e.Status)} · {E(e.Outcome)}</p><p>Źródło: {E(e.Source)}</p><pre>{E(e.Note)}</pre></section>");foreach(var l in study.Logs){h.Append($"<section><h2>{E(l.Name)}</h2><p>{E(l.Source)} · {E(l.Status)}</p><pre>{E(Analyzer.Summary(l))}</pre>");foreach(var m in l.Markers)h.Append($"<p>{m.Time:F3} s ±{m.Uncertainty:F3} s: {E(m.Label)} · {E(m.Source)}</p>");h.Append("</section>");}h.Append("<p>Brak połączenia bezpośredniego z ECU. Obserwacje nie są diagnozą uszkodzenia części.</p></html>");Store.WriteAtomic(p.FileName,h.ToString());Process.Start(new ProcessStartInfo(p.FileName){UseShellExecute=true});}
 void Settings(){Heading("Ustawienia");Page.Children.Add(Card(Stack(Txt("Magazyn danych",21),Txt(Store.Root),Txt("Zapis atomowy oraz kopia poprzedniego zapisu .bak. Surowe logi są kopiowane do katalogu pojazdu/badania; oryginały pozostają bez zmian."),Row(Btn("Otwórz folder danych",()=>{Directory.CreateDirectory(Store.Root);Process.Start(new ProcessStartInfo(Store.Root){UseShellExecute=true});}),Btn("Zapisz teraz",()=>Persist()),Btn("Eksportuj kopię badania",ExportStudy),Btn("Wczytaj badanie",OpenStudy)))));Page.Children.Add(Card(Stack(Txt("VCDS",21),Txt("Folder logów: "+VcdsLogsDirectory()),Btn("Otwórz folder logów",()=>Process.Start(new ProcessStartInfo(VcdsLogsDirectory()){UseShellExecute=true})))));}
}
public class FormDialog:Window {
 public StackPanel Body=new();public Action? Accept;
 public FormDialog(Window owner,string title){Owner=owner;Title=title;Width=750;Height=680;MinWidth=550;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=MainWindow.B("#091321");Foreground=Brushes.White;FontSize=16;Resources=owner.Resources;var dock=new DockPanel{Margin=new Thickness(24)};var controls=MainWindow.Row(MainWindow.Btn("Zapisz",()=>{try{Accept?.Invoke();DialogResult=true;}catch(Exception e){MessageBox.Show(e.Message);}}),MainWindow.Btn("Anuluj",()=>DialogResult=false));DockPanel.SetDock(controls,Dock.Bottom);dock.Children.Add(controls);Body.Children.Add(MainWindow.Txt(title,24));dock.Children.Add(new ScrollViewer{Content=Body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=dock;}
 public TextBox Field(string label,string value="",bool multiline=false){Body.Children.Add(MainWindow.Txt(label,14));var t=new TextBox{Text=value,AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};if(multiline)t.Height=120;Body.Children.Add(t);return t;}
 public ComboBox Select(string label,string[] items,string? value=null){Body.Children.Add(MainWindow.Txt(label,14));var c=new ComboBox{ItemsSource=items,SelectedItem=value??items[0]};Body.Children.Add(c);return c;}
}