using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace DiagScope2;
public partial class App:Application {
 protected override void OnStartup(StartupEventArgs e){base.OnStartup(e);DispatcherUnhandledException+=(s,args)=>{MessageBox.Show("Operacja nie powiodła się. Sprawdź zapis badania przed kontynuowaniem.\n"+args.Exception.Message,"DiagScope");args.Handled=true;};
 if(e.Args.Contains("--review-smoke"))ReviewSmoke();
 if(e.Args.Contains("--smoke-test")){Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{try{var w=(MainWindow)MainWindow;string dir=Path.Combine(Environment.GetEnvironmentVariable("DIAGSCOPE_DATA_ROOT")!,"ui");Directory.CreateDirectory(dir);var nav=typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;foreach(string page in new[]{"Diagnostyka","Pomiary","Analizator logów","LPG","Pojazdy","Błędy DTC","Wyniki","Ustawienia"}){nav.Invoke(w,[page]);w.UpdateLayout();Capture(w,Path.Combine(dir,page+".png"));}var log=Importer.Read(@"C:\Dev\DiagScope2.Tests\fixtures\real-vcds.csv");var graph=new LogWindow(log,()=>{},w.Resources);graph.Show();graph.UpdateLayout();Capture(graph,Path.Combine(dir,"Wykres.png"));graph.Close();File.WriteAllText(Path.Combine(dir,"result.txt"),"PASS: 8 ekranów i okno rzeczywistego logu wyrenderowane.");Shutdown(0);}catch(Exception ex){File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("DIAGSCOPE_DATA_ROOT")!,"ui-error.txt"),ex.ToString());Shutdown(2);}}));}}
 static void Capture(Window w,string path){var bitmap=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(w);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}