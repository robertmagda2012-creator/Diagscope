using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace DiagScope2.ReceiverHost;
public static class TransportTest {
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool CreatePipe(out IntPtr read,out IntPtr write,IntPtr attributes,uint size);
 public static int Run() {
  if(!CreatePipe(out var read,out var write,IntPtr.Zero,0))throw new IOException("CreatePipe failed");
  string dir=Path.Combine(Path.GetTempPath(),"DiagScope-pipe-test-"+Guid.NewGuid().ToString("N"));
  using var store=new Store(dir);using var handle=new SafeFileHandle(write,true);
  using var writer=new FileStream(handle,FileAccess.Write);
  var receiver=new Receiver(read,store,()=>{});var task=Task.Run(receiver.Run);
  string payload="TEST-ONLY,G001,F0,800,/min,"+string.Concat(Enumerable.Repeat("G000,,,,",11));
  var packet=new byte[513];Encoding.ASCII.GetBytes(payload).CopyTo(packet,0);
  // Explicit fragmentation exercises partial reads across the 512-byte packet.
  writer.Write(packet,0,11);writer.Flush();Thread.Sleep(50);
  writer.Write(packet,11,packet.Length-11);writer.Flush();
  Thread.Sleep(3800);
  using(var status=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"status.json"))))
   if(!status.RootElement.GetProperty("stale").GetBoolean())throw new Exception("Stale detection failed");
  writer.Write(new byte[]{254,253});writer.Flush();
  if(!task.Wait(5000)){receiver.Stop();throw new Exception("Receiver did not close");}
  store.Dispose();
  var records=File.ReadAllLines(Path.Combine(dir,"pomiary.jsonl")).Select(line=>JsonDocument.Parse(line)).ToArray();
  try {
   if(records.Count(d=>d.RootElement.GetProperty("type").GetString()=="frame")!=1)throw new Exception("Unexpected frame count");
   if(!records.Any(d=>d.RootElement.TryGetProperty("status",out var s)&&s.GetString()=="closed"))throw new Exception("Missing closed state");
   if(!records.Any(d=>d.RootElement.TryGetProperty("status",out var s)&&s.GetString()=="stopped"))throw new Exception("Missing stopped state");
   if(new FileInfo(Path.Combine(dir,"surowe.jsonl")).Length==0)throw new Exception("Raw bytes were not preserved");
  }finally{foreach(var record in records)record.Dispose();}
  Console.WriteLine("PASS x86 Windows pipe: fragmented frame, raw persistence, stale timeout, stopped, closed. Synthetic transport only; not VCDS.");
  return 0;
 }
}