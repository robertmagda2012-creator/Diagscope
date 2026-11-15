using System.Diagnostics; using System.Globalization; using System.Runtime.InteropServices; using System.Text; namespace DiagScope2.Protocol {  public sealed class Sample {
    public string id, group, name, unit, raw;
    public int field;
    public double? value;
    public double time;
  }
  public sealed class Frame {
    public string ecu, received_utc;
    public List<Sample> samples = new List<Sample>();
  }
  public static class Wire {
    public static Frame Parse(byte[] bytes) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      string text = Encoding.GetEncoding(1252).GetString(bytes).Trim();
      string[] tokens = text.Split(',');
      if (tokens.Length < 6 || String.IsNullOrWhiteSpace(tokens[0]) || !text.EndsWith(","))
        throw new FormatException("Niepełna ramka pomiarów; zachowano surowe dane.");
      Frame f = new Frame { ecu = tokens[0], received_utc = DateTime.UtcNow.ToString("o") };
      int p = 1, slots = 0;
      bool zeroBlock = false;
      while (p < tokens.Length - 1) {
        slots++;
        string group = tokens[p++].Trim();
        if (group == "G000") {
          // Public VC-Scope source writes G000,,,, for a disabled slot.
          for (int k = 0; k < 3; k++) {
            if (p >= tokens.Length || tokens[p++].Length != 0) throw new FormatException("Nieznany pusty blok.");
          }
          continue;
        }
        int groupNumber;
        if (group.Length != 4 || group[0] != 'G' ||
            (group != "GZER" && !Int32.TryParse(group.Substring(1), out groupNumber)))
          throw new FormatException("Nieznany format grupy: " + group);
        if(group=="GZER")zeroBlock=true;
        if (p + 2 >= tokens.Length) throw new FormatException("Brakuje pól ramki.");
        string fieldToken = tokens[p++];
        int field;
        if (!fieldToken.StartsWith("F") || !Int32.TryParse(fieldToken.Substring(1), out field) || field < 0 || field > 9)
          throw new FormatException("Nieznany format pola.");
        if(group!="GZER"&&field>3)throw new FormatException("Pole poza układem zwykłej grupy.");
        string raw = tokens[p++];
        string unit = tokens[p++].Trim();
        string id = group + ":F" + field;
        if (f.samples.Any(s => s.id == id)) throw new FormatException("Powtórzone pole w ramce.");
        double number;
        double? value = null;
        // Units that describe bit masks are categorical, even when the text is digits.
        bool bits = unit.IndexOf("bit", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!bits && Double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && !Double.IsNaN(number) && !Double.IsInfinity(number)) value = number;
        f.samples.Add(new Sample { id=id, group=group.Substring(1), field=field,
          name="Grupa " + group.Substring(1) + " · pole " + (field+1), unit=unit, raw=raw, value=value });
      }
      if(slots!=(zeroBlock?18:12))throw new FormatException("Niepełny lub nieobsługiwany układ ramki: " + slots + " pól zamiast " + (zeroBlock?18:12) + ".");
      if(zeroBlock&&f.samples.Count(s=>s.group=="ZER")!=10)throw new FormatException("Niepełna grupa zerowa.");
      if (f.samples.Count == 0) throw new FormatException("Ramka nie zawiera pomiarów.");
      return f;
    }
  }
  public sealed class Decoder {
    readonly List<byte> pending = new List<byte>();
    readonly Action<byte[]> payload;
    readonly Action<string> control, warning;
    bool discard, ended;
    public Decoder(Action<byte[]> payload, Action<string> control, Action<string> warning) {
      this.payload=payload; this.control=control; this.warning=warning;
    }
    void Finish() {
      if (!discard && pending.Count > 0) payload(pending.ToArray());
      pending.Clear(); discard=false;
    }
    public void Feed(byte[] buffer, int count) {
      if(count<0||count>buffer.Length)throw new ArgumentOutOfRangeException(nameof(count));if(ended)return;
      for (int i=0; i<count; i++) {
        byte b=buffer[i];
        if (b == 0 || b == 255) { Finish(); continue; }
        if (b == 254 || b == 253) {
          if (pending.Count > 0) { warning("Sterowanie przerwało niepełną ramkę."); pending.Clear(); }
          discard=false; control(b == 254 ? "stopped" : "closed");
          if(b==253){ended=true;return;}
          continue;
        }
        if (discard) continue;
        pending.Add(b);
        if (pending.Count > 2048) {
          warning("Brak rozpoznanego końca ramki; dane zachowano wyłącznie jako surowe.");
          pending.Clear(); discard=true;
        }
      }
    }
    public void End() { ended=true;
      if (pending.Count > 0) warning("Koniec połączenia z niepełną ramką; nie uznano jej za pomiar.");
      pending.Clear();
    }
  }
  public static class Native {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct StartupInfo {
      public int cb;
      public IntPtr reserved, desktop, title;
      public int x,y,xsize,ysize,xchars,ychars,fill,flags;
      public short show;
      public ushort reservedSize;
      public IntPtr reservedBytes, stdin, stdout, stderr;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern void GetStartupInfoW(out StartupInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool PeekNamedPipe(IntPtr h, IntPtr b, uint n, IntPtr read, out uint available, IntPtr left);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static IntPtr InheritedPipe(string directory) {
      string file=Path.Combine(directory, "MB-PIPE.TXT");
      if (!File.Exists(file)) return IntPtr.Zero;
      int fd;
      if (!Int32.TryParse(File.ReadAllText(file).Trim(), out fd) || fd < 0) return IntPtr.Zero;
      StartupInfo info; GetStartupInfoW(out info);
      if (info.reservedBytes == IntPtr.Zero || info.reservedSize < 4) return IntPtr.Zero;
      int count=Marshal.ReadInt32(info.reservedBytes);
      // Private CRT startup layout: only supported for an x86 parent/child pair.
      if (IntPtr.Size != 4 || count < 1 || count > 4096 || fd >= count || 4L + count * 5L > info.reservedSize)
        return IntPtr.Zero;
      byte flags=Marshal.ReadByte(info.reservedBytes, 4+fd);
      if ((flags & 1) == 0 || (flags & 8) == 0) return IntPtr.Zero;
      IntPtr h=new IntPtr(Marshal.ReadInt32(info.reservedBytes, 4+count+4*fd));
      return GetFileType(h) == 3 ? h : IntPtr.Zero;
    }
  }
}