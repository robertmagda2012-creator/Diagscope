using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiagScope2.Protocol;
namespace DiagScope2.ReceiverHost;

public sealed class Store:IDisposable {
 readonly string directory;
 readonly StreamWriter raw,events;
 readonly Stopwatch elapsed=Stopwatch.StartNew();
 string status="waiting";
 public bool Failed=>status=="error";
 long sequence;
 double? lastFrame;
 public Store(string directory) {
  this.directory=directory;Directory.CreateDirectory(directory);
  raw=Writer("surowe.jsonl");events=Writer("pomiary.jsonl");Snapshot();
 }
 StreamWriter Writer(string name)=>new(new FileStream(Path.Combine(directory,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
 void Event(object value)=>events.WriteLine(JsonSerializer.Serialize(value));
 public void Raw(byte[] b,int n)=>raw.WriteLine(JsonSerializer.Serialize(new{received_utc=DateTimeOffset.UtcNow,time=elapsed.Elapsed.TotalSeconds,count=n,base64=Convert.ToBase64String(b,0,n)}));
 public void Warn(string message)=>Event(new{type="warning",message,received_utc=DateTimeOffset.UtcNow,time=elapsed.Elapsed.TotalSeconds});
 public void State(string value){status=value;Event(new{type="status",status,received_utc=DateTimeOffset.UtcNow,time=elapsed.Elapsed.TotalSeconds});Snapshot();}
 public void Accept(Frame f) {
  double time=elapsed.Elapsed.TotalSeconds;lastFrame=time;status="active";
  Event(new{type="frame",schema_version=2,source="VCDS VC-Scope experimental",sequence=++sequence,ecu=f.ecu,
   received_utc=f.received_utc,time_basis="receiver_elapsed",
   samples=f.samples.Select(s=>new{s.id,s.group,s.field,s.name,s.unit,s.raw,s.value,time}).ToArray()});
 }
 public void Snapshot() {
  double? age=lastFrame.HasValue?elapsed.Elapsed.TotalSeconds-lastFrame:null;
  string path=Path.Combine(directory,"status.json"),tmp=path+".tmp";
  File.WriteAllText(tmp,JsonSerializer.Serialize(new{status,frames=sequence,age_seconds=age,
   stale=!age.HasValue||age>3||status!="active",time_basis="receiver_elapsed",generated_utc=DateTimeOffset.UtcNow}));
  File.Move(tmp,path,true);
 }
 public void Dispose(){events.Dispose();raw.Dispose();}
}
public static class Program {
 public static int Main(string[] args) {
  if(args.Contains("--transport-self-test"))return TransportTest.Run();
  if(args.Contains("--help")){Console.WriteLine("Eksperymentalny odbiornik VC-Scope x86. Uruchamiany wyłącznie przez VCDS z odziedziczonym potokiem. Nie instaluje się sam. --output DIRECTORY ustala nowy folder sesji.");return 0;}
  // Fail closed: no inherited descriptor means no probing or fallback to ECU/COM.
  IntPtr pipe=Native.InheritedPipe(Environment.CurrentDirectory);
  if(pipe==IntPtr.Zero){Console.Error.WriteLine("Brak poprawnego odziedziczonego potoku x86. Niczego nie podłączono.");return 2;}
  int output=Array.IndexOf(args,"--output");
  string directory=output>=0&&output+1<args.Length?Path.GetFullPath(args[output+1]):Path.Combine(
   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DiagScope2","Receiver",
   DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
  try {
   using var store=new Store(directory);var receiver=new Receiver(pipe,store,()=>{});
   Console.CancelKeyPress+=(s,e)=>{e.Cancel=true;receiver.Stop();};
   receiver.Run();return store.Failed?1:0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
 }
}