using System.Diagnostics; using System.Runtime.InteropServices; using DiagScope2.Protocol; namespace DiagScope2.ReceiverHost {  public sealed class Receiver {
    readonly IntPtr handle;
    readonly Store store;
    readonly Action done;
    volatile bool cancel;
    public Receiver(IntPtr handle,Store store,Action done) {this.handle=handle;this.store=store;this.done=done;}
    public void Stop() {cancel=true;}
    public void Run() {
      bool closed=false;
      Decoder decoder=new Decoder(bytes=> {
        try {store.Accept(Wire.Parse(bytes));}catch(FormatException e){store.Warn(e.Message);}
      }, state=>{ store.State(state); if(state=="closed"){closed=true;cancel=true;} }, store.Warn);
      try {
        // Match VC-Scope's producer contract: one marker byte, then 512 bytes.
        // VCDS refreshes the pipe while FF padding is consumed slowly. Draining
        // all available bytes reaches unrelated tail bytes (observed at offset
        // 996 in 1020-byte reads) and produces false FE/FD controls.
        byte[] buffer=new byte[512];
        int packetRemaining=0;
        Stopwatch snapshotClock=Stopwatch.StartNew();
        while(!cancel) {
          if(snapshotClock.ElapsedMilliseconds>=500){store.Snapshot();snapshotClock.Restart();}
          uint available;
          if(!Native.PeekNamedPipe(handle,IntPtr.Zero,0,IntPtr.Zero,out available,IntPtr.Zero)) {
            int err=Marshal.GetLastWin32Error();
            if(err==109 || err==232){store.State("disconnected");break;}
            throw new IOException("Nie można odczytać pipe, błąd Windows " + err);
          }
          if(available==0){Thread.Sleep(20);continue;}
          uint read;
          bool marker=packetRemaining==0;
          uint requested=marker?1:Math.Min((uint)packetRemaining,available);
          if(!Native.ReadFile(handle,buffer,requested,out read,IntPtr.Zero))
            throw new IOException("Odczyt przerwany, błąd Windows " + Marshal.GetLastWin32Error());
          if(read==0){store.State("disconnected");break;}
          if(marker) {
            byte first=buffer[0];
            if(first!=0 && first!=255 && first!=254 && first!=253)packetRemaining=512;
          } else packetRemaining-=(int)read;
          store.Raw(buffer,(int)read); decoder.Feed(buffer,(int)read);
          if(marker && packetRemaining==0 && !cancel)Thread.Sleep(50);
        }
        decoder.End();
        if(cancel&&!closed)store.State("stopped");
      } catch(Exception e) {store.Warn(e.Message);store.State("error");}
      finally { Native.CloseHandle(handle); store.Snapshot(); if(done!=null)done(); }
    }
  }
}