using DiagScope2;
using DiagScope2.Analysis;
using DiagScope2.Protocol;
using System.Text;
using Decoder=DiagScope2.Protocol.Decoder;
using System.Text.Json;
using System.Diagnostics;
using Channel=DiagScope2.Channel;
public static class Extended {
 static int tests;
 static void Check(bool yes,string name){if(!yes)throw new Exception("FAIL "+name);tests++;Console.WriteLine("PASS "+name);}
 static void Reject(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}catch(FormatException){Check(true,name);return;}throw new Exception("FAIL expected rejection "+name);}
 static Channel Series(int n,Func<int,double> value,Func<int,double>? time=null)=>new(){Name="test-only",Clock="synthetic",Unit="unit",Samples=Enumerable.Range(0,n).Select(i=>new PointSample{Time=time?.Invoke(i)??i,Value=value(i),Raw=value(i).ToString(System.Globalization.CultureInfo.InvariantCulture),Row=i+1}).ToList()};
 public static void Run(){
  Reject(()=>Importer.Vcds(""),"empty CSV rejected");
  Reject(()=>Importer.Csv("t;v\n0;\"unfinished",';'),"truncated quote rejected");
  Check(Importer.Separator("\n\nt;v\n0;1")==';',"blank leading lines");
  Check(Importer.Number("NaN")==null&&Importer.Number("Infinity")==null,"nonfinite rejected");
  var l=new LogData{Name="test-only",Channels=[Series(6,i=>i,i=>i<3?i:i+10)]};
  var report=Engine.Analyze(l);
  Check(report.Findings.Any(f=>f.Kind=="gap"&&f.Evidence[0].Start==2&&f.Evidence[0].End==13),"evidence exact gap interval");
  Check(!report.Findings.Any(f=>f.Kind=="trend"),"no trend across gap with short segments");
  Check(report.Findings.All(f=>f.Evidence.Length>0&&f.Tests.Length>0&&f.Limitations.Length>0),"every finding has evidence tests limits");
  var quantized=Engine.Analyze(new LogData{Channels=[Series(100,i=>Math.Floor(i/5.0))]});
  Check(!quantized.Findings.Any(f=>f.Kind=="jump"),"repeated quantized steps are not reported as anomalies");
  var sustained=Engine.Analyze(new LogData{Channels=[Series(30,i=>i<15?1:100)]});
  Check(!sustained.Findings.Any(f=>f.Kind=="jump"),"sustained level change is not treated as a one-sample spike");
  var anomaly=Engine.Analyze(new LogData{Channels=[Series(30,i=>i==15?100:1)]});
  Check(anomaly.Findings.Count(f=>f.Kind=="jump"&&f.Status=="HIPOTEZA")==2,"isolated spike remains a hypothesis");
  var a=Series(100,i=>Math.Sin(i*.37));var b=Series(100,i=>2*Math.Sin(i*.37)+5,i=>i+3);
  var pair=Engine.Compare(a,b,new(0,0,"test"),3,.01);
  Check(pair.Pairs==100&&pair.Pearson>.999999&&pair.Spearman>.999999,"known lag and rank correlation");
  var constant=Engine.Compare(a,Series(100,i=>1),new(0,0,"test"),0,.1);
  Check(constant.Pearson==null,"constant channel correlation undefined");
  Reject(()=>Engine.Compare(a,Series(20,i=>i,i=>i%3),new(0,0,"test"),0,.1),"reversed clocks rejected");
  Reject(()=>Engine.Compare(a,b,new(0,-1,"test"),0,.1),"negative uncertainty rejected");
  var align=Alignment.FromMarkers(new(){Time=10,Uncertainty=.2},new(){Time=13,Uncertainty=.3});
  Check(align.Offset==3&&align.Uncertainty==.5,"manual alignment uncertainty sum");
  var uncertain=Engine.Order(l,new(){Time=2,Uncertainty=1},l,new(){Time=5.1,Uncertainty=1},align);
  Check(uncertain.Title.Contains("nierozstrzygnięta"),"overlapping event intervals unresolved");
  var disjoint=Engine.Compare(a,b,new(1000,0,"test"),0,.1);
  Check(disjoint.Pairs==0&&disjoint.Pearson==null,"no overlap no correlation");
  var js=Importer.JsonLines("""
{"type":"frame","ecu":"A","time_basis":"receiver_elapsed","samples":[{"id":"x","name":"X","time":1,"raw":"010","unit":"bits"}]}
{"type":"frame","ecu":"A","time_basis":"receiver_elapsed","samples":[{"id":"x","name":"X","time":2,"raw":"1","unit":"V"}]}
{"type":"frame","ecu":"B","time_basis":"receiver_elapsed","samples":[{"id":"x","name":"X","time":0,"raw":"1","unit":"V"}]}
{"type":"user_marker","time":2,"label":"LPG","origin":"manual"}
{"type":"frame",
""");
  Check(js.Channels.Count==3,"units and ECU separated");
  Check(js.Channels[0].Samples[0].Value==null&&js.Channels[0].TextState,"bitmask never numeric");
  Check(js.Channels.All(c=>c.Clock.Contains("nie czas ECU")),"receipt clock retained");
  Check(js.Markers.Count==1&&js.Warnings.Count>=2,"markers and partial row retained");
  string payload="ECU,G001,F0,800,/min,"+string.Concat(Enumerable.Repeat("G000,,,,",11));
  var bytes=Encoding.ASCII.GetBytes(payload);Check(Wire.Parse(bytes).samples.Count==1,"wire ordinary group");
  Reject(()=>Wire.Parse(Encoding.ASCII.GetBytes("ECU,G001,F0,800,/min,")),"incomplete slots rejected");
  int frames=0,warnings=0;var controls=new List<string>();
  var decoder=new Decoder(p=>{Wire.Parse(p);frames++;},controls.Add,_=>warnings++);
  foreach(byte value in bytes.Concat(new byte[]{0,254,253}))decoder.Feed([value],1);
  Check(frames==1&&controls.SequenceEqual(new[]{"stopped","closed"}),"fragmentation and controls");
  decoder.Feed(bytes,bytes.Length);Check(frames==1,"ignore after close");
  var malformed=new Decoder(_=>throw new Exception("oversize frame accepted"),_=>{},_=>warnings++);
  malformed.Feed(Enumerable.Repeat((byte)'a',2100).ToArray(),2100);malformed.Feed([0],1);
  Check(warnings==1,"bounded decoder oversize");
  var fixture=Importer.Read(Path.Combine(AppContext.BaseDirectory,"fixtures","real-vcds.csv"));
  var root=Path.Combine(Path.GetTempPath(),"DiagScope-review-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  Environment.SetEnvironmentVariable("DIAGSCOPE_DATA_ROOT",root);
  var study=new Study{Logs=[fixture]};Store.PreserveSource(fixture,study);
  Check(File.ReadAllBytes(fixture.OriginalPath).SequenceEqual(File.ReadAllBytes(fixture.StoredFile)),"preserved source byte-identical");
  Store.Save(study);Store.Save(study);
  Check(Store.Startup().Logs[0].Hash==fixture.Hash,"durable study roundtrip");
  string locked=Path.Combine(root,"locked.json");File.WriteAllText(locked,"original");
  using(var file=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
   bool failed=false;try{Store.WriteAtomic(locked,"replacement");}catch(IOException){failed=true;}
   Check(failed,"locked destination reports failure");
  }
  Check(File.ReadAllText(locked)=="original"&&Directory.GetFiles(root,"*.tmp").Length==0,"failed save keeps old file and cleans temporary");
  int csv=0,json=0,rawFrames=0,rawWarnings=0;var importRoot=@"C:\DiagScope\Importy-Z-Lenovo\2026-11-08\Rozpakowane";
  if(Directory.Exists(importRoot)) {
  foreach(var file in Directory.EnumerateFiles(Path.Combine(importRoot,"VCDS","VCDS-CSV"),"*.CSV")) {
   var real=Importer.Read(file);var r=Engine.Analyze(real);Check(real.Rows>0&&r.Findings.Count>0,"real CSV "+Path.GetFileName(file));csv++;
  }
  foreach(var file in Directory.EnumerateFiles(importRoot,"pomiary.jsonl",SearchOption.AllDirectories).Where(f=>new FileInfo(f).Length>0)) {
   try{var real=Importer.Read(file);Check(real.Rows>0,"real JSONL "+Path.GetFileName(Path.GetDirectoryName(file)));json++;}
   catch(FormatException){Console.WriteLine("SKIP unsupported/empty measurement "+file);}
  }
  foreach(var file in Directory.EnumerateFiles(importRoot,"surowe.jsonl",SearchOption.AllDirectories).Where(f=>new FileInfo(f).Length>0)) {
   var dec=new Decoder(p=>{try{Wire.Parse(p);rawFrames++;}catch(FormatException){rawWarnings++;}},_=>{},_=>rawWarnings++);
   foreach(var line in File.ReadLines(file))try{using var doc=JsonDocument.Parse(line);byte[] chunk=Convert.FromBase64String(doc.RootElement.GetProperty("base64").GetString()!);dec.Feed(chunk,chunk.Length);}catch(JsonException){rawWarnings++;}
   dec.End();
  }
  Check(rawFrames>0,"recorded real raw frames decoded");
  Console.WriteLine($"REAL: CSV {csv}; JSONL {json}; raw frames {rawFrames}; rejected/warnings {rawWarnings}");
  }else Console.WriteLine("SKIP external real corpus not present; bundled real fixture tested.");
  var perf=new LogData{Channels=[Series(100000,i=>Math.Sin(i*.01))]};
  var sw=Stopwatch.StartNew();var result=Engine.Analyze(perf);sw.Stop();
  Check(sw.Elapsed.TotalSeconds<15,"100k sample analysis under 15 s");Console.WriteLine($"PERF 100000 samples {sw.Elapsed.TotalMilliseconds:F1} ms; findings {result.Findings.Count}");
  var cancelled=new CancellationToken(true);bool canceled=false;try{Engine.Analyze(perf,cancelled);}catch(OperationCanceledException){canceled=true;}Check(canceled,"analysis cancellation");
  Console.WriteLine($"EXTENDED TESTS PASSED {tests}");
 }
}