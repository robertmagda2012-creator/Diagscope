namespace DiagScope2.Protocol;
public record SourceCapability(string Id,string Name,bool Import,bool LiveVerified,string Limitation);
public static class Sources {
 public static readonly SourceCapability[] Available=[
  new("vcds","VCDS",true,false,"CSV; eksperymentalny VC-Scope x86 wymaga testów z autem."),
  new("diagscope","DiagScope",true,false,"JSONL i zapisane badania."),
  new("lpgtech316","LPGTECH TECH 316",true,false,"CSV z mapowaniem / lpg_ui_frame; brak przetestowanego protokołu sterownika.")
 ];
}
public interface IFrameSource {
 string SourceId {get;}
 IAsyncEnumerable<Frame> ReadAsync(CancellationToken cancellationToken);
}