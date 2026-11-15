using System.Globalization;
using System.Text;
namespace DiagScope2.Analysis;

public record Evidence(string LogId,string Source,string Hash,string Parameter,string Unit,string Clock,
 double? Start,double? End,int[] Rows);
public record Finding(string Kind,string Status,string Title,string Reason,Evidence[] Evidence,string[] Tests,string[] Limitations) {
 public string Display=>$"{Status} · {Title} · {Evidence.FirstOrDefault()?.Parameter} · {Evidence.FirstOrDefault()?.Start:G6} s";
}
public sealed class AnalysisReport {
 public string Version {get;set;}="1.0";
 public DateTimeOffset CreatedUtc {get;set;}=DateTimeOffset.UtcNow;
 public List<Finding> Findings {get;set;}=[];
 public List<string> Limitations {get;set;}=["Wyniki opisują zapis, nie potwierdzają uszkodzenia części.",
 "Progi statystyczne są heurystyczne; wartości graniczne wymagają danych właściwego sterownika.",
 "Zegary źródeł pozostają odrębne. Czas odbioru nie jest czasem ECU."];
 public string Text() {
  var b=new StringBuilder($"Analizator {Version} · {CreatedUtc:o}\n");
  foreach(var f in Findings) {
   b.AppendLine($"\n{f.Status} · {f.Title}\n{f.Reason}");
   foreach(var e in f.Evidence) b.AppendLine($"Źródło: {e.Source} · ID {e.LogId} · SHA {e.Hash}\nParametr: {e.Parameter} [{e.Unit}] · {e.Clock} · [{e.Start?.ToString("G9",CultureInfo.InvariantCulture)??"brak"}, {e.End?.ToString("G9",CultureInfo.InvariantCulture)??"brak"}] s · wiersze: {string.Join(",",e.Rows)}");
   b.AppendLine("Testy: "+string.Join(" | ",f.Tests));
   b.AppendLine("Ograniczenia: "+string.Join(" | ",f.Limitations));
  }
  b.AppendLine("\n"+string.Join("\n",Limitations));return b.ToString();
 }
}
public record Alignment(double Offset,double Uncertainty,string Basis) {
 // To compare against A, transform B time to B - Offset.
 public static Alignment FromMarkers(Marker a,Marker b) {
  if(!double.IsFinite(a.Time)||!double.IsFinite(b.Time)||!double.IsFinite(a.Uncertainty)||!double.IsFinite(b.Uncertainty)||a.Uncertainty<0||b.Uncertainty<0)
   throw new ArgumentException("Znaczniki muszą mieć skończony czas i nieujemną niepewność.");
  return new(b.Time-a.Time,a.Uncertainty+b.Uncertainty,$"Ręczne wspólne zdarzenie: {a.Label} / {b.Label}; źródła {a.Source} / {b.Source}. Bez korekcji dryfu.");
 }
}
public record PairResult(double Lag,int Pairs,double? Pearson,double? Spearman,double Start,double End,double MatchTolerance);
public static class Engine {
 const int MaxFindings=2000;
 static bool Finite(double? n)=>n.HasValue&&double.IsFinite(n.Value);
 static double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a.Length==0?0:a.Length%2==1?a[a.Length/2]:(a[a.Length/2-1]+a[a.Length/2])/2;}
 static Evidence Ev(LogData l,Channel c,double? a,double? b,params int[] rows)=>new(l.Id,l.Name+" · "+l.Source,l.Hash,c.Name,c.Unit,c.Clock,a,b,rows);
 public static AnalysisReport Analyze(LogData log,CancellationToken cancel=default) {
  var report=new AnalysisReport();
  foreach(var warning in log.Warnings.Take(100))report.Limitations.Add(warning);
  void Add(Finding f){if(report.Findings.Count<MaxFindings)report.Findings.Add(f);}
  foreach(var c in log.Channels) {
   cancel.ThrowIfCancellationRequested();
   var timed=c.Samples.Where(s=>Finite(s.Time)).ToArray();
   double? start=timed.Length==0?null:timed.Min(s=>s.Time),end=timed.Length==0?null:timed.Max(s=>s.Time);
   var intervals=new List<double>();int invalid=0,missing=0,reverse=0,duplicate=0;
   for(int i=0;i<c.Samples.Count;i++) {
    var s=c.Samples[i];
    if(!Finite(s.Time))invalid++;
    if(string.IsNullOrWhiteSpace(s.Raw)||(!c.TextState&&!Finite(s.Value)))missing++;
    if(i==0||!Finite(s.Time)||!Finite(c.Samples[i-1].Time))continue;
    double dt=s.Time!.Value-c.Samples[i-1].Time!.Value;
    if(dt>0)intervals.Add(dt);else if(dt==0)duplicate++;else reverse++;
   }
   double median=Median(intervals);
   Add(new("quality","POTWIERDZONE","Jakość zapisu: "+c.Name,
    $"Próbki {c.Samples.Count}; brak/poprawność czasu {invalid}; brak wartości {missing}; cofnięcia {reverse}; duplikaty {duplicate}; mediana Δt {median:G6} s.",
    [Ev(log,c,start,end)],["Porównaj z oryginalnym plikiem i ustawieniami rejestracji."],["Potwierdzenie dotyczy wyłącznie zawartości logu."]));
   if(invalid+missing+reverse+duplicate>0)Add(new("hypothesis","HIPOTEZA","Możliwy problem ciągłości lub formatu",
    "Niepełne wartości lub nieciągła oś czasu mogą wynikać z zapisu, restartu sesji albo interpretacji formatu.",
    [Ev(log,c,start,end)],["Powtórz zapis w samym programie źródłowym.","Sprawdź wiersze źródłowe przed oceną czujnika."],["Nie ustalono miejsca powstania problemu."]));
   for(int i=1;i<c.Samples.Count;i++) {
    if((i&4095)==0)cancel.ThrowIfCancellationRequested();
    var a=c.Samples[i-1];var b=c.Samples[i];
    if(!Finite(a.Time)||!Finite(b.Time)||b.Time<=a.Time)continue;
    double dt=b.Time!.Value-a.Time!.Value;
    if(median>0&&dt>3*median)Add(new("gap","POTWIERDZONE","Luka czasowa w zapisie",
     $"Δt={dt:G6} s > 3 × mediana {median:G6} s.",[Ev(log,c,a.Time,b.Time,a.Row,b.Row)],
     ["Porównaj pozostałe kanały i zapis źródłowy w tym przedziale."],["Luka nie dowodzi utraty danych ECU; nie interpolowano przez lukę."]));
    if(c.TextState&&a.Raw!=b.Raw)Add(new("state","POTWIERDZONE","Zmiana zapisanego stanu",
     $"{a.Raw} → {b.Raw}",[Ev(log,c,a.Time,b.Time,a.Row,b.Row)],
     ["Potwierdź znaczenie stanu w dokumentacji źródła."],["Zmiana nastąpiła pomiędzy próbkami; dokładny moment jest nieznany."]));
   }
   var numeric=c.TextState?[]:c.Samples.Where(s=>Finite(s.Time)&&Finite(s.Value)).ToArray();
   if(numeric.Length<5||reverse+duplicate+invalid>0)continue;
   // Compute per contiguous segment: never bridge invalid rows or gaps.
   var segments=new List<List<PointSample>>();List<PointSample>? segment=null;PointSample? prev=null;
   foreach(var s in c.Samples) {
    if(!Finite(s.Time)||!Finite(s.Value)){segment=null;prev=null;continue;}
    if(segment==null||prev==null||s.Time<=prev.Time||(median>0&&s.Time-prev.Time>3*median)){segment=[];segments.Add(segment);}
    segment.Add(s);prev=s;
   }
   foreach(var seg in segments.Where(s=>s.Count>=5)) {
    double origin=seg[0].Time!.Value,meanT=seg.Average(s=>s.Time!.Value-origin),meanV=seg.Average(s=>s.Value!.Value);
    double variance=seg.Sum(s=>Math.Pow(s.Time!.Value-origin-meanT,2));
    double slope=variance>0?seg.Sum(s=>(s.Time!.Value-origin-meanT)*(s.Value!.Value-meanV))/variance:0;
    if(double.IsFinite(slope))Add(new("trend","POTWIERDZONE","Trend liczbowy: "+c.Name,
     $"Nachylenie regresji {slope:G6} {c.Unit}/s; próbek {seg.Count}.",
     [Ev(log,c,seg[0].Time,seg[^1].Time,seg[0].Row,seg[^1].Row)],
     ["Porównaj przy jednakowych warunkach pracy i obciążeniu."],["Opis statystyczny; brak oceny normy i istotności trendu."]));
    var diffs=seg.Zip(seg.Skip(1),(a,b)=>b.Value!.Value-a.Value!.Value).ToArray();
    double center=Median(diffs),mad=Median(diffs.Select(x=>Math.Abs(x-center)));
    double threshold=6*1.4826*mad;
    for(int i=0;i<diffs.Length;i++)if(Math.Abs(diffs[i]-center)>Math.Max(threshold,1e-9)) {
     Add(new("jump","HIPOTEZA","Kandydat na skok / anomalię",
      $"Zmiana {diffs[i]:G6} {c.Unit}; odchylenie od mediany przyrostów przekracza 6 × skalowany MAD.",
      [Ev(log,c,seg[i].Time,seg[i+1].Time,seg[i].Row,seg[i+1].Row)],
      ["Sprawdź zmianę obciążenia, stan paliwa oraz sąsiednie parametry."],["Przy MAD=0 wykrywana jest każda niezerowa różnica od typowego przyrostu; nie jest to rozpoznanie usterki."]));
    }
   }
  }
  if(report.Findings.Count==MaxFindings)report.Limitations.Add("Limit 2000 wniosków: dalsze zdarzenia pominięto w raporcie; źródło pozostaje kompletne.");
  return report;
 }
 static double? Corr(double[] x,double[] y) {
  if(x.Length<5)return null;double ax=x.Average(),ay=y.Average(),xx=0,yy=0,xy=0;
  for(int i=0;i<x.Length;i++){double a=x[i]-ax,b=y[i]-ay;xx+=a*a;yy+=b*b;xy+=a*b;}
  double r=xy/Math.Sqrt(xx*yy);return xx>0&&yy>0&&double.IsFinite(r)?Math.Clamp(r,-1,1):null;
 }
 static double[] Ranks(double[] a) {
  var sorted=a.Select((v,i)=>(v,i)).OrderBy(p=>p.v).ToArray();var ranks=new double[a.Length];
  for(int i=0;i<sorted.Length;){int j=i+1;while(j<sorted.Length&&sorted[j].v==sorted[i].v)j++;for(int k=i;k<j;k++)ranks[sorted[k].i]=(i+j-1)/2.0;i=j;}return ranks;
 }
 public static PairResult Compare(Channel a,Channel b,Alignment alignment,double lag,double tolerance,CancellationToken cancel=default) {
  if(!double.IsFinite(lag)||!double.IsFinite(tolerance)||tolerance<=0||!double.IsFinite(alignment.Offset)||!double.IsFinite(alignment.Uncertainty)||alignment.Uncertainty<0)
   throw new ArgumentException("Nieprawidłowe parametry synchronizacji.");
  PointSample[] Valid(Channel c) {
   if(c.TextState)throw new ArgumentException("Korelacja wymaga kanałów liczbowych.");
   var p=c.Samples.Where(s=>Finite(s.Time)&&Finite(s.Value)).ToArray();
   for(int i=1;i<p.Length;i++)if(p[i].Time<=p[i-1].Time)throw new ArgumentException("Najpierw rozdziel sesje z cofnięciem/duplikacją czasu.");
   return p;
  }
  var x=Valid(a);var y=Valid(b);var xs=new List<double>();var ys=new List<double>();int j=0;double first=0,last=0;
  // Greedy monotone nearest match without sample reuse, extrapolation or interpolation.
  for(int i=0;i<x.Length&&j<y.Length;i++) {
   if((i&4095)==0)cancel.ThrowIfCancellationRequested();
   double target=x[i].Time!.Value+alignment.Offset+lag;
   while(j+1<y.Length&&Math.Abs(y[j+1].Time!.Value-target)<Math.Abs(y[j].Time!.Value-target))j++;
   if(Math.Abs(y[j].Time!.Value-target)>tolerance)continue;
   if(xs.Count==0)first=x[i].Time!.Value;last=x[i].Time!.Value;
   xs.Add(x[i].Value!.Value);ys.Add(y[j].Value!.Value);j++;
  }
  var xa=xs.ToArray();var ya=ys.ToArray();
  return new(lag,xs.Count,Corr(xa,ya),Corr(Ranks(xa),Ranks(ya)),first,last,tolerance);
 }
 public static AnalysisReport CompareLogs(LogData a,Channel ca,LogData b,Channel cb,Alignment align,double maxLag,double step,double tolerance,CancellationToken cancel=default) {
  if(!double.IsFinite(maxLag)||!double.IsFinite(step)||maxLag<0||step<=0||2*maxLag/step>100)throw new ArgumentException("Wybierz maksymalnie 101 przesunięć, krok > 0.");
  var r=new AnalysisReport();var results=new List<PairResult>();
  for(double lag=-maxLag;lag<=maxLag+step*1e-8;lag+=step)results.Add(Compare(ca,cb,align,lag,tolerance,cancel));
  var best=results.Where(p=>p.Pearson.HasValue).OrderByDescending(p=>Math.Abs(p.Pearson!.Value)).FirstOrDefault();
  r.Findings.Add(new("correlation",best==null?"DO SPRAWDZENIA":"HIPOTEZA","Porównanie parametrów i przesunięć",
   align.Basis+$"\nPrzesunięcie B−A {align.Offset:G6} s ±{align.Uncertainty:G6} s; tolerancja par {tolerance:G6} s. Dodatni lag oznacza późniejszą próbkę B.\n"+
   string.Join("\n",results.Select(p=>$"lag {p.Lag:G6} s: par {p.Pairs}, Pearson {p.Pearson?.ToString("F4")??"brak"}, Spearman {p.Spearman?.ToString("F4")??"brak"}")),
   [Ev(a,ca,best?.Start,best?.End),Ev(b,cb,best==null?null:best.Start+align.Offset+best.Lag,best==null?null:best.End+align.Offset+best.Lag)],
   ["Powtórz pomiar w porównywalnych warunkach.","Zweryfikuj wspólne zdarzenie i dryf zegarów; porównaj drugi znacznik."],
   ["Korelacja nie jest przyczynowością.","Wybór maksimum spośród wielu przesunięć zwiększa ryzyko przypadkowej zależności; brak testu istotności.",
    "Niepewność synchronizacji nie jest uwzględniana jako korekta współczynnika. Nie wnioskuj kolejności przy nakładających się przedziałach.",
    "Pary bez interpolacji; minimum 5 par, kanał stały nie ma zdefiniowanej korelacji."]));
  return r;
 }
 public static Finding Order(LogData a,Marker ma,LogData b,Marker mb,Alignment align) {
  _ = Alignment.FromMarkers(ma,mb);
  if(!double.IsFinite(align.Offset)||!double.IsFinite(align.Uncertainty)||align.Uncertainty<0)throw new ArgumentException("Nieprawidłowa synchronizacja.");
  double delta=mb.Time-align.Offset-ma.Time,u=ma.Uncertainty+mb.Uncertainty+align.Uncertainty;
  return new("order","HIPOTEZA",Math.Abs(delta)<=u?"Kolejność nierozstrzygnięta":delta>0?"Zdarzenie A poprzedza B":"Zdarzenie B poprzedza A",
   $"Różnica B−A {delta:G6} s ±{u:G6} s.",
   [new(a.Id,a.Name,a.Hash,ma.Label,"s","znacznik",ma.Time,ma.Time,[]),new(b.Id,b.Name,b.Hash,mb.Label,"s","znacznik",mb.Time,mb.Time,[])],
   ["Sprawdź źródło obu znaczników i niezależny punkt synchronizacji."],["Ręczny znacznik nie dowodzi fizycznego przełączenia ani przyczyny usterki."]);
 }
}