using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Startwerk;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Length>=2&&e.Args[0]=="--iso-page-test")
        {
            base.OnStartup(e);MainWindow? main=null;IsoDownloadWindow? download=null;
            try
            {
                main=new MainWindow();main.Show();download=new IsoDownloadWindow(main,true);download.Show();
                var completed=await Task.WhenAny(download.MicrosoftReady,Task.Delay(TimeSpan.FromSeconds(60)));
                if(completed!=download.MicrosoftReady||!await download.MicrosoftReady)throw new IOException("Microsoft-Seite konnte im eingebauten Downloadfenster nicht erfolgreich geladen werden.");
                File.WriteAllText(e.Args[1],"PASS: Published application loads the official Microsoft Windows ISO page using WebView2. No ISO downloaded, no installation started.");Shutdown(0);
            }
            catch(Exception ex){File.WriteAllText(e.Args[1],"FAIL: "+ex);Shutdown(1);}
            finally{download?.Close();main?.Close();}
            return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--apply-update")
        {
            base.OnStartup(e);
            try{await Task.Run(()=>Updates.Apply(e.Args[1]));Shutdown(0);}
            catch(Exception ex){MessageBox.Show(ex.Message,"Startwerk-Update fehlgeschlagen",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
            return;
        }
        if(e.Args.Length>=2 && e.Args[0]=="--usb-selftest")
        {
            base.OnStartup(e);var messages=new List<string>();int exitCode=0;
            try
            {
                await Backend.TestEmbeddedUsb(messages.Add);
                bool rejected=false;
                try { await Backend.WriteUsb("",new Target("test","Invalid",-1,"invalid",1,"invalid"),false,true,messages.Add); }
                catch(IOException){rejected=true;}
                if(!rejected)throw new IOException("Ungueltiger USB-Auftrag wurde nicht abgewiesen.");
                messages.Add("PASS: Embedded module and JSON transport tested without external PS1. Invalid device request rejected before any write.");
            }
            catch(Exception ex){messages.Add("FAIL: "+ex);exitCode=1;}
            File.WriteAllLines(e.Args[1],messages);Shutdown(exitCode);return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--preview")
        {
            base.OnStartup(e);
            var window = new MainWindow(); MainWindow = window; window.Show();
            await Task.Delay(2500); window.UpdateLayout();
            Window target=window;
            if(e.Args.Length>=3 && e.Args[2]=="linux") {((System.Windows.Controls.RadioButton)window.FindName("RawMode")).IsChecked=true;await Task.Delay(1000);window.UpdateLayout();}
            var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(target); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(e.Args[1])) encoder.Save(stream);
            Shutdown(); return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
