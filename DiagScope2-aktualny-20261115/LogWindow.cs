using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
namespace DiagScope2;
public class LogWindow:Window {
 readonly LogData log;readonly Action save;readonly bool hasLpg;HashSet<Channel>? focused; (double Start,double End)? evidence;readonly StackPanel chartStack=new(),legend=new();readonly TextBlock position=MainWindow.Txt("Najedź na wykres, aby odczytać czas i wartość.",13);double from=0,to=1;int lastClickLane;double? selectedTime;
 public LogWindow(LogData data,Action changed,ResourceDictionary resources,bool hasLpg=false){log=data;save=changed;this.hasLpg=hasLpg;Title="DiagScope · "+log.Name;Width=890;Height=625;MinWidth=660;MinHeight=470;ResizeMode=ResizeMode.CanResize;Background=MainWindow.B("#091321");Foreground=Brushes.White;Resources=resources;FontSize=16;
 var dock=new DockPanel{Margin=new Thickness(15)};var top=MainWindow.Stack(MainWindow.Txt(log.Name,24),MainWindow.Txt(log.Source+" · Osie czasu oryginalne, osobna skala każdego parametru",13),MainWindow.Row(MainWindow.Btn("Cały log",ShowFull),MainWindow.Btn("Powiększ +",()=>Zoom(.65)),MainWindow.Btn("Oddal −",()=>Zoom(1.5)),MainWindow.Btn("←",()=>Pan(-.25)),MainWindow.Btn("→",()=>Pan(.25)),MainWindow.Btn("+ Znacznik",AddMarker),MainWindow.Btn("Ciągłość",QualityView)),position);DockPanel.SetDock(top,Dock.Top);dock.Children.Add(top);
 var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(220)});grid.Children.Add(new ScrollViewer{Content=chartStack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});var side=new ScrollViewer{Content=legend,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(20,0,0,0)};Grid.SetColumn(side,1);grid.Children.Add(side);dock.Children.Add(grid);Content=dock;Refresh();}
 public void Focus(IEnumerable<Channel> channels,double? start,double? end)
 {
  focused=channels.ToHashSet();
  evidence=start.HasValue&&end.HasValue&&double.IsFinite(start.Value)&&double.IsFinite(end.Value)
   ? (Math.Min(start.Value,end.Value),Math.Max(start.Value,end.Value)) : null;
  from=0;to=1;
  Refresh();
 }
 public void ShowFull(){focused=null;evidence=null;from=0;to=1;Refresh();}
 public void Refresh()
 {
  chartStack.Children.Clear();legend.Children.Clear();
  legend.Children.Add(MainWindow.Txt("Parametry",20));
  string? lastGroup=null;
  for(int i=0;i<log.Channels.Count;i++)
  {
   var ch=log.Channels[i];
   int colorIndex=i;
   bool visible=focused?.Contains(ch)??ch.Visible;
   var groupAddress=System.Text.RegularExpressions.Regex.Match(ch.Address,@"^(?<group>\d{3})\.(?<field>[1-9]\d*)$");
   string group=groupAddress.Success?groupAddress.Groups["group"].Value:"";
   if(group!=lastGroup&&group.Length>0)
    legend.Children.Add(new TextBlock
    {
     Text="Grupa "+group,Foreground=MainWindow.B("#A8C9ED"),FontWeight=FontWeights.SemiBold,
     Margin=new Thickness(0,lastGroup==null?0:8,0,6)
    });
   lastGroup=group;
   string label=LogMetadata.ChannelLabel(ch);
   if(groupAddress.Success&&log.Channels.Any(other=>other!=ch&&
      other.Address.StartsWith(group+".",StringComparison.Ordinal)&&other.DisplayLabel==label))
    label+=" · "+ch.Address;
   else if(!groupAddress.Success&&ch.Address.Length>0)label+=" · "+ch.Address;
   var check=new CheckBox
   {
    IsChecked=visible,
    Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White},
    ToolTip=string.Join(" · ",new[]{ch.Address,ch.OriginalName,ch.Clock}.Where(s=>s.Length>0))
   };
   void Toggle(bool show)
   {
    if(focused!=null){if(show)focused.Add(ch);else focused.Remove(ch);}
    else{ch.Visible=show;save();}
    Refresh();
   }
   check.Checked+=(s,e)=>Toggle(true);
   check.Unchecked+=(s,e)=>Toggle(false);
   var edit=new MenuItem{Header="Zmień nazwę parametru"};
   edit.Click+=(s,e)=>Rename(ch);
   check.ContextMenu=new ContextMenu{Items={edit}};
   legend.Children.Add(new Border{Child=check,BorderBrush=Plot.Palette[i%Plot.Palette.Length],BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(8,0,0,0),Margin=new Thickness(0,0,0,10)});
   if(!visible)continue;
   var plot=new Plot(ch,log.Markers,from,to,colorIndex,evidence){Height=210,Margin=new Thickness(0,0,0,15)};
   plot.Hover=(time,sample)=>position.Text=$"{LogMetadata.ChannelLabel(ch)} · {time:F3} s · próbka: {sample?.Time:F3} s → {sample?.Raw??"brak"} · {ch.Clock}";
   plot.Picked=t=>{selectedTime=t;lastClickLane=colorIndex;position.Text=$"Wybrano {t:F3} s · {ch.Name}";};
   chartStack.Children.Add(plot);
  }
  if(chartStack.Children.Count==0)chartStack.Children.Add(MainWindow.Txt("Włącz parametr po prawej."));
  legend.Children.Add(MainWindow.Txt("Znaczniki",20));
  foreach(var marker in log.Markers)
  {
   string interval=marker.IsAutomatic
     ? $"{marker.Time-marker.Uncertainty:F3}–{marker.Time+marker.Uncertainty:F3} s"
     : $"{marker.Time:F3} s ±{marker.Uncertainty:F3} s";
   legend.Children.Add(MainWindow.Txt($"{marker.Label}\n{interval}\n{marker.Source}",13));
  }
 }
 void Rename(Channel channel)
 {
  var dialog=new FormDialog(this,"Nazwa parametru");
  var name=dialog.Field("Nazwa",channel.Name=="Kanał bez opisu"?"":channel.Name);
  if(channel.Address.Length>0)dialog.Body.Children.Add(MainWindow.Txt("Identyfikator źródłowy: "+channel.Address,14));
  dialog.Accept=()=>{
   string label=name.Text.Trim();
   if(label.Length==0)throw new ArgumentException("Wpisz nazwę parametru.");
   LogMetadata.SaveName(channel,label);
   channel.Name=label;
   save();
  };
  if(dialog.ShowDialog()==true)Refresh();
 }

 void Zoom(double factor){double mid=(from+to)/2,range=Math.Clamp((to-from)*factor,.001,1);from=Math.Max(0,mid-range/2);to=Math.Min(1,from+range);from=Math.Max(0,to-range);Refresh();}
 void Pan(double factor){double range=to-from;from=Math.Clamp(from+range*factor,0,1-range);to=from+range;Refresh();}
 void AddMarker(){var d=new FormDialog(this,"Znacznik zdarzenia");var time=d.Field("Czas w osi logu [s]",selectedTime?.ToString("F3",CultureInfo.InvariantCulture)??"");var label=d.Field("Zdarzenie",hasLpg?"Benzyna → LPG":"");var uncertainty=d.Field("Niepewność [s]");var source=d.Field("Źródło obserwacji");d.Accept=()=>{var t=Importer.Time(time.Text);var u=Importer.Number(uncertainty.Text);if(!t.HasValue||!u.HasValue||u<0||string.IsNullOrWhiteSpace(source.Text)||string.IsNullOrWhiteSpace(label.Text))throw new Exception("Podaj zdarzenie, czas, nieujemną niepewność i źródło.");log.Markers.Add(new(){Time=t.Value,Uncertainty=u.Value,Label=label.Text,Source=source.Text});save();};if(d.ShowDialog()==true)Refresh();}
 void QualityView(){var d=new FormDialog(this,"Ciągłość danych");var t=d.Field("Wynik obliczeń",Analyzer.Summary(log),true);t.Height=410;t.IsReadOnly=true;foreach(var ch in log.Channels){var q=Analyzer.Analyze(ch);foreach(var gap in q.Gaps.Take(20))d.Body.Children.Add(MainWindow.Txt($"{ch.Name}: {gap.Start:F3}–{gap.End:F3} s, przerwa {gap.Duration:F3} s (wiersz {gap.Row})",13));}d.ShowDialog();}
}
public class Plot:FrameworkElement {
 public static Brush[] Palette=[MainWindow.B("#57B4FF"),MainWindow.B("#65DFB4"),MainWindow.B("#F4C66B"),MainWindow.B("#C19AFF"),MainWindow.B("#FF879E"),MainWindow.B("#82D7EF")];
 readonly Channel channel;readonly List<Marker> markers;readonly double from,to;readonly int color;readonly Quality quality;readonly string[] categories;readonly double minT,maxT,minY,maxY;readonly (double Start,double End)? evidence;double pointer=-1;public Action<double,PointSample?>? Hover;public Action<double>? Picked;
 public Plot(Channel c,List<Marker> m,double f,double t,int index,(double Start,double End)? evidence=null){this.evidence=evidence;channel=c;markers=m;from=f;to=t;color=index;quality=Analyzer.Analyze(c);categories=c.Samples.Where(p=>p.Raw.Length>0).Select(p=>p.Raw).Distinct().ToArray();var times=c.Samples.Where(p=>p.Time.HasValue).Select(p=>p.Time!.Value).ToArray();double lo=times.Length>0?times.Min():0,hi=times.Length>0?times.Max():1;if(evidence is { } r){double context=Math.Max((hi-lo)*.02,(r.End-r.Start)*.25);lo=Math.Max(lo,r.Start-context);hi=Math.Min(hi,r.End+context);if(hi<lo){lo=times.Length>0?times.Min():0;hi=times.Length>0?times.Max():1;}}minT=lo+(hi-lo)*f;maxT=lo+(hi-lo)*t;if(maxT==minT)maxT=minT+1;var vals=c.Samples.Where(p=>p.Time>=minT&&p.Time<=maxT&&p.Value.HasValue).Select(p=>p.Value!.Value).ToArray();minY=c.TextState?0:vals.Length>0?vals.Min():0;maxY=c.TextState?Math.Max(1,categories.Length-1):vals.Length>0?vals.Max():1;if(minY==maxY){minY-=.5;maxY+=.5;}MouseMove+=(s,e)=>{pointer=e.GetPosition(this).X;double time=TimeAt(pointer);var near=channel.Samples.Where(p=>p.Time.HasValue).MinBy(p=>Math.Abs(p.Time!.Value-time));Hover?.Invoke(time,near);InvalidateVisual();};MouseLeave+=(s,e)=>{pointer=-1;InvalidateVisual();};MouseLeftButtonDown+=(s,e)=>Picked?.Invoke(TimeAt(e.GetPosition(this).X));ToolTip="Kliknij, aby wybrać czas znacznika. Przyciski nad wykresem zmieniają zakres.";}
 double TimeAt(double x)=>minT+Math.Clamp((x-95)/Math.Max(1,ActualWidth-120),0,1)*(maxT-minT);
 void Text(DrawingContext dc,string text,double x,double y,int size=12,Brush? brush=null){dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush??Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(50,ActualWidth-x-8),MaxTextHeight=42,Trimming=TextTrimming.CharacterEllipsis},new Point(x,y));}
 protected override void OnRender(DrawingContext dc){double w=ActualWidth;if(w<140)return;dc.DrawRoundedRectangle(MainWindow.B("#0B1727"),new Pen(MainWindow.B("#304760"),1),new Rect(0,0,w,ActualHeight),8,8);Text(dc,LogMetadata.ChannelLabel(channel),95,8,14,Palette[color%Palette.Length]);Text(dc,channel.Clock,95,30,11);double X(double v)=>95+(v-minT)/(maxT-minT)*(w-120);double Y(double v)=>162-(v-minY)/(maxY-minY)*100;var gridPen=new Pen(MainWindow.B("#314962"),1);for(int i=0;i<5;i++){double x=95+i*(w-120)/4;dc.DrawLine(gridPen,new(x,57),new(x,163));Text(dc,(minT+(maxT-minT)*i/4).ToString("F2")+" s",Math.Max(0,x-20),174,11);}for(int i=0;i<3;i++){double val=minY+(maxY-minY)*i/2;double y=Y(val);dc.DrawLine(gridPen,new(95,y),new(w-25,y));string label=channel.TextState?categories.ElementAtOrDefault((int)Math.Round(val))??"":val.ToString("G5");Text(dc,label,5,y-8,11);}
 dc.PushClip(new RectangleGeometry(new Rect(94,54,w-118,112)));if(evidence is { } marked){double a=X(marked.Start),b=X(marked.End);dc.DrawRectangle(MainWindow.B("#315D8D"),null,new Rect(a,54,Math.Max(2,b-a),111));}var geom=new StreamGeometry();using(var g=geom.Open()){PointSample? prev=null;double py=0;foreach(var p in channel.Samples){double? value=channel.TextState?Array.IndexOf(categories,p.Raw):p.Value;if(!p.Time.HasValue||!value.HasValue||p.Raw.Length==0||p.Time<minT||p.Time>maxT){prev=null;continue;}var point=new Point(X(p.Time.Value),Y(value.Value));bool broken=prev==null||p.Time<=prev.Time||quality.Median>0&&p.Time-prev.Time>3*quality.Median;if(broken)g.BeginFigure(point,false,false);else{if(channel.TextState)g.LineTo(new Point(point.X,py),true,false);g.LineTo(point,true,false);}prev=p;py=point.Y;}}geom.Freeze();dc.DrawGeometry(null,new Pen(Palette[color%Palette.Length],1.3),geom);
 foreach(var marker in markers)
 {
  if(marker.IsAutomatic)
  {
   double a=Math.Max(minT,marker.Time-marker.Uncertainty);
   double b=Math.Min(maxT,marker.Time+marker.Uncertainty);
   if(b>a)dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(68,244,198,107)),null,new Rect(X(a),54,X(b)-X(a),111));
  }
  else if(marker.Time>=minT&&marker.Time<=maxT)
  {
   var pen=new Pen(Brushes.White,1){DashStyle=DashStyles.Dash};
   dc.DrawLine(pen,new(X(marker.Time),54),new(X(marker.Time),165));
  }
 }if(pointer>=95&&pointer<=w-25)dc.DrawLine(new Pen(Brushes.White,.8),new(pointer,54),new(pointer,165));dc.Pop();}
}
public class MappingDialog:FormDialog {
 public LogData? Result;readonly string path,text;List<string[]> rows=[];readonly ComboBox separator;readonly TextBox header,start,timecol;readonly StackPanel mapping=new();readonly List<(int,CheckBox,TextBox)> choices=[];
 public MappingDialog(Window owner,string file):base(owner,"Import CSV — wybór kolumn"){path=file;byte[] sourceBytes=Importer.ReadBytes(file);text=Importer.Decode(sourceBytes);Width=900;Height=800;separator=Select("Separator",["Średnik ;","Przecinek ,","Tabulator"],Importer.Separator(text) switch{';'=>"Średnik ;",'\t'=>"Tabulator",_=>"Przecinek ,"});header=Field("Wiersz nazw kolumn (od 1)","1");start=Field("Pierwszy wiersz danych (od 1)","2");timecol=Field("Numer kolumny czasu (od 1), czas w sekundach lub hh:mm:ss","1");Body.Children.Add(MainWindow.Btn("Pokaż podgląd i kolumny",Build));Body.Children.Add(mapping);Build();Accept=()=>{if(!int.TryParse(header.Text,out int h)||!int.TryParse(start.Text,out int s)||!int.TryParse(timecol.Text,out int t))throw new Exception("Wpisz numery wierszy i kolumny.");var selected=choices.Where(x=>x.Item2.IsChecked==true).Select(x=>(Col:x.Item1,Unit:x.Item3.Text)).ToArray();if(selected.Length==0)throw new Exception("Wybierz co najmniej jeden parametr.");if(h<1||h>rows.Count||t<1||rows.Count==0||t>rows[h-1].Length)throw new Exception("Nieprawidłowa kolumna czasu.");var log=Importer.Mapped(text,Sep(),h-1,s-1,t-1,selected,"CSV — mapowanie użytkownika");log.Name=Path.GetFileName(path);log.OriginalPath=path;log.Hash=Convert.ToHexString(SHA256.HashData(sourceBytes));log.Warnings.Add("Mapowanie użytkownika. Zweryfikuj jednostki i kolumnę czasu. Dla grup z różnymi zegarami użyj automatycznego importu VCDS.");Result=log;};}
 char Sep()=>separator.SelectedIndex switch{0=>';',1=>',',_=>'\t'};
 void Build(){rows=Importer.Csv(text,Sep());mapping.Children.Clear();choices.Clear();mapping.Children.Add(MainWindow.Txt("Pierwsze wiersze (bez zmiany oryginału)",18));mapping.Children.Add(new TextBox{Text=string.Join("\n",rows.Take(8).Select((r,i)=>$"{i+1}: "+string.Join(" | ",r))),IsReadOnly=true,AcceptsReturn=true,Height=140,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});if(!int.TryParse(header.Text,out int h)||h<1||h>rows.Count)return;mapping.Children.Add(MainWindow.Txt("Zaznacz przebiegi, wpisz jednostki. Nie zaznaczaj czasu jako parametru.",14));for(int i=0;i<rows[h-1].Length;i++){var check=new CheckBox{Content=$"{i+1}: {rows[h-1][i]}",Width=420};var unit=new TextBox{Width=150,ToolTip="Jednostka (np. rpm, ms, V, stan)"};mapping.Children.Add(MainWindow.Row(check,unit));choices.Add((i,check,unit));}}
}