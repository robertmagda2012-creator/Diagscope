using System.Text.Json;
namespace DiagScope2;
public static class JsonLogReader {
 static string S(JsonElement o,string key)=>o.TryGetProperty(key,out var v)?v.ValueKind==JsonValueKind.String?v.GetString()??"":v.ToString():"";
 public static LogData Read(string text) {
  var log=new LogData{Source="DiagScope JSONL"};var channels=new Dictionary<string,Channel>();DateTimeOffset? origin=null;int row=0,segment=0;
  var last=new Dictionary<string,double>();string previousEcu="";
  var fuelSwitches=new LpgFuelSwitches();int offlineLpgRows=0;
  foreach(string line in text.Split('\n')) {
   row++;if(string.IsNullOrWhiteSpace(line))continue;
   try {
    using var document=JsonDocument.Parse(line);var root=document.RootElement;string type=S(root,"type");
    if(type=="frame") {
     string ecu=S(root,"ecu"),basis=S(root,"time_basis");
     if(log.Rows==0){log.Ecu=ecu;log.Vin=LogMetadata.ValidVin(S(root,"vin"));}
     if(DateTimeOffset.TryParse(S(root,"received_utc"),out var recorded))log.RecordedAt??=recorded;
     if(previousEcu.Length>0&&ecu!=previousEcu){segment++;last.Clear();}previousEcu=ecu;
     string clock=basis=="receiver_elapsed"?"czas odbioru monotoniczny, nie czas ECU":basis=="log_group_time"?"czas grupy z logu":basis.Length>0?basis:"czas próbki: podstawa nieokreślona";
     var items=root.GetProperty("samples").EnumerateArray().ToArray();
     bool reset=items.Any(p=>Importer.Time(S(p,"time")) is double t&&last.TryGetValue(S(p,"id"),out var prev)&&t<prev);
     if(reset){segment++;last.Clear();log.Warnings.Add($"Wiersz {row}: cofnięcie czasu; nowy segment {segment}.");}
     foreach(var p in items) {
      double? t=Importer.Time(S(p,"time"));string actualClock=clock;
      if(t==null&&DateTimeOffset.TryParse(S(root,"received_utc"),out var utc)){origin??=utc;t=(utc-origin.Value).TotalSeconds;actualClock="czas odbioru UTC, nie czas ECU";}
      string id=S(p,"id"),name=S(p,"name"),unit=S(p,"unit"),raw=S(p,"raw");if(raw.Length==0)raw=S(p,"value");if(id.Length==0)id=name;
      string group=S(p,"group"),field=S(p,"field");
      string address=group.Length>0&&int.TryParse(field,out var fieldNumber)?$"{group}.{fieldNumber+1}":id;
      string label=name.Length>0&&!name.StartsWith("Grupa ",StringComparison.OrdinalIgnoreCase)?name:"";
      Add($"{segment}|{ecu}|{id}|{unit}|{name}|{actualClock}",label,unit,actualClock+$" · segment {segment} · ECU {ecu}",t,raw,address,group.Length>0&&field.Length>0?"VCDS:"+address:"",ecu);
      if(t.HasValue)last[id]=t.Value;
     }log.Rows++;
    }else if(type=="lpg_ui_frame") {
     log.Source="LPGTECH / odczyt interfejsu Gas Setting";
     if(!DateTimeOffset.TryParse(S(root,"received_utc"),out var utc))throw new FormatException("Brak daty odbioru.");
     log.RecordedAt??=utc;origin??=utc;double t=(utc-origin.Value).TotalSeconds;
     var values=root.GetProperty("values");
     string connection=S(root,"connection").Trim();
     bool offline=connection.StartsWith("Brak połączenia",StringComparison.OrdinalIgnoreCase)||
       connection.StartsWith("Rozłącz",StringComparison.OrdinalIgnoreCase)||
       connection.StartsWith("Szukanie",StringComparison.OrdinalIgnoreCase)||
       Importer.Number(S(values,"battery")) is <=0;
     if(offline){fuelSwitches.Reset();offlineLpgRows++;continue;}
     string fuel=LpgFuelSwitches.Normalize(S(root,"fuel"));
     foreach(var p in values.EnumerateObject()) {
      string unit=root.TryGetProperty("units",out var u)?S(u,p.Name):"";
      Add($"lpg|{p.Name}|{unit}",LpgFuelSwitches.ChannelName(p.Name),unit,"czas odbioru UI, nie czas ECU",t,p.Value.ToString());
     }
     Add("lpg|fuel","Stan paliwa","stan","czas odbioru UI, nie czas ECU",t,fuel);
     if(fuelSwitches.Read(fuel,utc,origin.Value) is { } marker)log.Markers.Add(marker);
     log.Rows++;
    }else if(type=="user_marker") {
     var t=Importer.Time(S(root,"time"));if(t==null)throw new FormatException("Brak czasu znacznika.");
     double? u=Importer.Number(S(root,"uncertainty"));
     log.Markers.Add(new(){Time=t.Value,Label=S(root,"label"),Source="JSONL: "+S(root,"origin")+"; "+S(root,"time_basis")+(u==null?"; NIEZNANA NIEPEWNOŚĆ — uzupełnij przed synchronizacją":""),Uncertainty=u??0});
     if(u==null)log.Warnings.Add($"Wiersz {row}: znacznik bez niepewności; nie używaj do synchronizacji bez uzupełnienia.");
    }else if(type is "status" or "warning" or "vehicle_association")log.Warnings.Add($"Wiersz {row}: {type} · {S(root,"status")} {S(root,"message")}");
    else log.Warnings.Add($"Wiersz {row}: nierozpoznany typ {type}.");
   }catch(Exception e) when(e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException) {
    fuelSwitches.Reset();
    log.Warnings.Add($"Wiersz {row}: niepełny lub nieprawidłowy rekord JSONL.");
   }
  }
  if(offlineLpgRows>0)log.Warnings.Add($"Pominięto {offlineLpgRows} odczytów LPG bez połączenia lub z zerowym napięciem zasilania.");
  log.Channels=channels.Values.ToList();
  if(!log.Channels.Any(c=>c.Samples.Any(p=>p.Time.HasValue)))throw new FormatException("Brak poprawnych pomiarów JSONL.");
  return log;
  void Add(string key,string name,string unit,string clock,double? t,string raw,string address="",string channelKey="",string ecu="") {
   if(!channels.TryGetValue(key,out var c)){c=new(){Name=name.Length>0?name:"Kanał bez opisu",OriginalName=name,Address=address,Key=LogMetadata.KeyFor(ecu,channelKey),Unit=unit,Clock=clock,TextState=unit.Contains("bit",StringComparison.OrdinalIgnoreCase)||unit=="stan"};channels.Add(key,c);}
   double? value=c.TextState?null:Importer.Number(raw);
   c.Samples.Add(new(){Time=t,Value=value,Raw=raw,Row=row});
   if(raw.Length>0&&!Importer.Number(raw).HasValue)c.TextState=true;
  }
 }
}