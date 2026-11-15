using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DiagScope2.Analysis;
using Microsoft.Win32;
namespace DiagScope2;
public partial class MainWindow {
 CancellationTokenSource? analysisCancel;
 void AnalyzerPage() {
  Heading("Analizator logów");
  if(study.Logs.Count==0){Page.Children.Add(Card(Txt("Otwórz rzeczywisty log w Pomiarach, aby rozpocząć analizę.")));return;}
  var source=new ComboBox{ItemsSource=study.Logs,DisplayMemberPath="Name",SelectedIndex=0};
  var status=Txt("");var list=new ListBox{Height=220,DisplayMemberPath="Display"};
  var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=330,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  AnalysisReport? current=null;Action<Finding>? showComparison=null;
  void Show(AnalysisReport report,Action<Finding>? comparison=null){current=report;showComparison=comparison;list.ItemsSource=report.Findings;status.Text=$"Wnioski: {report.Findings.Count} · {report.CreatedUtc.ToLocalTime():HH:mm:ss}";if(report.Findings.Count>0)list.SelectedIndex=0;}
  list.SelectionChanged+=(s,e)=>{if(list.SelectedItem is Finding f){var r=new AnalysisReport{Findings=[f]};detail.Text=r.Text();}};
  async void Run(Func<CancellationToken,AnalysisReport> action,Action<Finding>? comparison=null) {
   analysisCancel?.Cancel();var cts=new CancellationTokenSource();analysisCancel=cts;var target=study;
   status.Text="Analiza trwa…";
   try {var result=await Task.Run(()=>action(cts.Token));if(!cts.IsCancellationRequested&&ReferenceEquals(target,study))Show(result,comparison);}
   catch(OperationCanceledException){status.Text="Anulowano.";}
   catch(Exception ex){status.Text="Analiza nie została ukończona: "+ex.Message;}
   finally{if(ReferenceEquals(analysisCancel,cts))analysisCancel=null;cts.Dispose();}
  }
  Page.Children.Add(Card(Stack(Txt("Analiza pojedynczego logu",21),source,Row(
   Btn("Analizuj",()=>{if(source.SelectedItem is LogData l)Run(ct=>Engine.Analyze(l,ct));}),
   Btn("Anuluj analizę",()=>analysisCancel?.Cancel()),
   Btn("Otwórz wykres",()=>{if(source.SelectedItem is LogData l)OpenLog(l);})))));
  var a=new ComboBox{ItemsSource=study.Logs,DisplayMemberPath="Name",SelectedIndex=0};
  var b=new ComboBox{ItemsSource=study.Logs,DisplayMemberPath="Name",SelectedIndex=Math.Min(1,study.Logs.Count-1)};
  var ca=new ComboBox{DisplayMemberPath="DisplayLabel"};var cb=new ComboBox{DisplayMemberPath="DisplayLabel"};
  var ma=new ComboBox();var mb=new ComboBox();
  void Bind(ComboBox log,ComboBox channel,ComboBox marker){if(log.SelectedItem is LogData l){channel.ItemsSource=l.Channels;channel.SelectedIndex=0;marker.ItemsSource=l.Markers;marker.SelectedIndex=l.Markers.Count>0?0:-1;}}
  a.SelectionChanged+=(s,e)=>Bind(a,ca,ma);b.SelectionChanged+=(s,e)=>Bind(b,cb,mb);Bind(a,ca,ma);Bind(b,cb,mb);
  var lag=new TextBox{Text="0"};var step=new TextBox{Text="0.2"};var tolerance=new TextBox{Text="0.1"};
  var orderA=new ComboBox();var orderB=new ComboBox();
  void OrderMarkers(){orderA.ItemsSource=(a.SelectedItem as LogData)?.Markers;orderB.ItemsSource=(b.SelectedItem as LogData)?.Markers;}
  a.SelectionChanged+=(s,e)=>OrderMarkers();b.SelectionChanged+=(s,e)=>OrderMarkers();OrderMarkers();
  (LogData A,Channel Ca,LogData B,Channel Cb,Alignment Align) Selection(){
   if(a.SelectedItem is not LogData x||b.SelectedItem is not LogData y||ca.SelectedItem is not Channel xc||cb.SelectedItem is not Channel yc)throw new ArgumentException("Wybierz logi i parametry.");
   Alignment alignment;
   if(x.Id==y.Id&&xc.Clock==yc.Clock)alignment=new(0,0,"Ten sam log i identyfikator zegara; nie dowodzi równoczesnego pomiaru ECU.");
   else {
    if(ma.SelectedItem is not Marker m||mb.SelectedItem is not Marker n)throw new ArgumentException("Wybierz znaczniki TEGO SAMEGO zdarzenia w obu źródłach.");
    if(m.Source.Contains("NIEZNANA NIEPEWNOŚĆ")||n.Source.Contains("NIEZNANA NIEPEWNOŚĆ"))throw new ArgumentException("Dodaj w wykresach nowe znaczniki z oszacowaną niepewnością.");
    alignment=Alignment.FromMarkers(m,n);
   }
   return(x,xc,y,yc,alignment);
  }
  var compareCard=Card(Stack(Txt("Porównanie parametrów i logów",21),Txt("Źródło A / parametr / wspólne zdarzenie synchronizacji",14),a,ca,ma,
   Txt("Źródło B / parametr / wspólne zdarzenie synchronizacji",14),b,cb,mb,
   Txt("Maksymalne przesunięcie ±s (0 = bez przeszukiwania)",14),lag,Txt("Krok przesunięcia [s]",14),step,
   Txt("Maksymalna odległość dopasowania próbek [s]",14),tolerance,
   Btn("Porównaj",()=>{var s=Selection();double l=Importer.Number(lag.Text)??throw new ArgumentException("Nieprawidłowe przesunięcie.");double st=Importer.Number(step.Text)??0;double t=Importer.Number(tolerance.Text)??0;Run(ct=>Engine.CompareLogs(s.A,s.Ca,s.B,s.Cb,s.Align,l,st,t,ct),f=>OpenComparison(s,f));}),
   Txt("Kolejność dwóch innych zdarzeń (A / B), po powyższej synchronizacji",14),orderA,orderB,
   Btn("Sprawdź kolejność",()=>{var s=Selection();if(orderA.SelectedItem is not Marker x||orderB.SelectedItem is not Marker y)throw new ArgumentException("Wybierz zdarzenia do porównania.");Show(new AnalysisReport{Findings=[Engine.Order(s.A,x,s.B,y,s.Align)]});})));
  Page.Children.Add(new Expander{Header="Porównaj parametry / logi / kolejność zdarzeń",Content=compareCard,Margin=new Thickness(0,0,0,18)});
  Page.Children.Add(Card(Stack(Txt("Zdarzenia, dowody i testy",21),status,list,detail,Row(
   Btn("Pokaż wniosek na wykresie",()=>{
    if(list.SelectedItem is not Finding finding)throw new InvalidOperationException("Wybierz wniosek.");
    if(showComparison!=null){showComparison(finding);return;}
    var first=finding.Evidence.FirstOrDefault();
    if(first==null)throw new InvalidOperationException("Brak przedziału lub kanału do pokazania.");
    var log=study.Logs.FirstOrDefault(x=>x.Id==first.LogId)??throw new InvalidOperationException("Nie znaleziono logu.");
    var channels=log.Channels.Where(c=>finding.Evidence.Any(e=>e.LogId==log.Id&&e.Parameter==c.Name&&e.Unit==c.Unit&&e.Clock==c.Clock)).ToArray();
    if(channels.Length==0)throw new InvalidOperationException("Kanał zmienił nazwę. Uruchom analizę ponownie.");
    OpenLogWindow(log).Focus(channels,first.Start,first.End);
   }),
   Btn("Zapisz wynik w historii",()=>{if(current==null)throw new Exception("Najpierw wykonaj analizę.");study.Entries.Add(new(){Title="Analizator logów "+current.Version,Source=string.Join(" / ",current.Findings.SelectMany(f=>f.Evidence).Select(e=>e.Source).Distinct()),Status="DO SPRAWDZENIA",Note=current.Text()});Persist();}),
   Btn("Eksport raportu JSON",()=>{if(current==null)throw new Exception("Najpierw wykonaj analizę.");var p=new SaveFileDialog{Filter="Raport JSON|*.json",FileName="DiagScope-analiza.json"};if(p.ShowDialog()==true)Store.WriteAtomic(p.FileName,JsonSerializer.Serialize(current,Store.Options));})
  ))));
 }
 void OpenComparison((LogData A,Channel Ca,LogData B,Channel Cb,Alignment Align) selected,Finding finding)
 {
  var first=finding.Evidence.FirstOrDefault();
  var overlay=new ComparisonPlot(
   selected.Ca, selected.Cb, selected.Align.Offset, first?.Start, first?.End);
  var panel=Stack(
   Txt(LogMetadata.ChannelLabel(selected.Ca)+" · "+selected.A.Name,16),
   Txt(LogMetadata.ChannelLabel(selected.Cb)+" · "+selected.B.Name,16),
   Txt($"Wyrównanie B względem A: {selected.Align.Offset:F3} s (±{selected.Align.Uncertainty:F3} s). "+selected.Align.Basis,13),
   overlay,Txt(finding.Title+" · "+finding.Status,14));
  panel.Margin=new Thickness(20);
  new Window{Owner=this,Title="Porównanie przebiegów",Width=1080,Height=620,MinWidth=720,MinHeight=480,
   Background=B("#091321"),Foreground=System.Windows.Media.Brushes.White,Resources=Resources,Content=panel}.Show();
 }
}