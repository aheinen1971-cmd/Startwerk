using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Startwerk;
public partial class MainWindow : Window
{
    bool busy;
    bool selectingSource;
    UsbImage? selectedImage;
    CompressedImage? preparedImage;
    string SelectedBoot => (BootChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? "UEFI";
    public bool InstallingUpdate {get;private set;}
    PreparedUpdate? pendingUpdate;
    bool checkingUpdates;
    readonly System.Windows.Threading.DispatcherTimer updateTimer=new(){Interval=TimeSpan.FromSeconds(30)};
    readonly System.Windows.Threading.DispatcherTimer websiteTimer=new(){Interval=TimeSpan.FromMinutes(1)};
    bool checkingWebsites;
    void OpenWebsites(object sender,RoutedEventArgs e){if(!busy)new WebsiteWindow(this).ShowDialog();}
    async Task CheckWebsites()
    {
        if(checkingWebsites||busy||InstallingUpdate)return;checkingWebsites=true;
        try
        {
            var previous=WebsiteSafety.Load(WebsiteMonitor.ReportPath);
            if(previous!=null){int warnings=previous.Results.Count(r=>r.State!="Verified");WebsiteButton.Content=warnings==0?"Webseiten: geprüft":$"Webseiten: {warnings} Hinweise";}
            if(!WebsiteMonitor.IsDue())return;
            var report=await WebsiteMonitor.Refresh();int problems=report.Results.Count(r=>r.State!="Verified");
            WebsiteButton.Content=problems==0?"Webseiten: geprüft":$"Webseiten: {problems} Hinweise";
            AddLog($"Webseitenprüfung abgeschlossen: {report.Results.Count(r=>r.State=="Verified")} Adressen bestätigt, {problems} Hinweise. Nächste Prüfung in 6 Stunden.");
            if(report.Results.Any(r=>r.State is "Blocked" or "Unavailable"))await CheckUpdatesOnStartup(true);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){AddLog("Webseitenprüfung: "+ex.Message);}
        finally{checkingWebsites=false;}
    }
    void OpenUpdates(object sender,RoutedEventArgs e){if(!busy)new UpdateWindow(this).ShowDialog();}
    bool UpdateIdle => !busy && !InstallingUpdate && OwnedWindows.Count==0 && string.IsNullOrWhiteSpace(ImagePath.Text);
    bool CanAutoInstall()
    {
        var settings=Updates.LoadSettings();return UpdateIdle&&settings.AutoInstall&&pendingUpdate!=null&&string.Equals(settings.Repository,pendingUpdate.Update.Payload.Repository,StringComparison.OrdinalIgnoreCase);
    }
    public void InstallPreparedUpdate(PreparedUpdate prepared)
    {
        if(busy||OwnedWindows.Cast<Window>().Any(w=>w is not UpdateWindow))throw new IOException("Bitte zuerst andere Aufgaben und Downloadfenster beenden.");
        Updates.BeginInstall(prepared);InstallingUpdate=true;updateTimer.Stop();Application.Current.Shutdown();
    }
    async Task CheckUpdatesOnStartup(bool force=false)
    {
        var settings=Updates.LoadSettings();
        if(checkingUpdates||!settings.AutoCheck||!force&&DateTime.UtcNow-settings.LastCheckUtc<TimeSpan.FromHours(12))return;
        checkingUpdates=true;
        try
        {
            var available=await Updates.Check(settings.Repository);settings.LastCheckUtc=DateTime.UtcNow;Updates.SaveSettings(settings);
            if(available==null)return;
            UpdateButton.Content="Update "+available.Payload.Version;AddLog("Update "+available.Payload.Version+" verfügbar.");
            var latestSettings=Updates.LoadSettings();
            if(latestSettings.AutoInstall&&string.Equals(latestSettings.Repository,available.Payload.Repository,StringComparison.OrdinalIgnoreCase)){pendingUpdate=await Updates.Download(available,AddLog);updateTimer.Start();if(CanAutoInstall())InstallPreparedUpdate(pendingUpdate);}
        }
        catch(Exception ex){AddLog("Update-Kanal: "+ex.Message);}
        finally{checkingUpdates=false;}
    }
    async void GetIso(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var window=new IsoDownloadWindow(this,WindowsMode.IsChecked==true);
        if(window.ShowDialog()==true&&window.Result is {} result)
        {
            await SelectImage(result.Path);
            AddLog(result.PublisherHashVerified?"Original-ISO heruntergeladen und mit der Anbieter-Prüfsumme bestätigt.":"Original-ISO von Microsoft geladen; keine Anbieter-Vergleichsprüfsumme angegeben.");
        }
    }
    public MainWindow()
    {
        InitializeComponent();Title="Startwerk · Version "+Updates.VersionText;
        AppVersion.Text="Version "+Updates.VersionText+"\n\nUSB-Sticks startklar.\nUpdates über GitHub.";
        ConfigureMode();UpdateBootInfo();
        updateTimer.Tick+=(_,_)=>{try{if(pendingUpdate!=null&&CanAutoInstall())InstallPreparedUpdate(pendingUpdate);}catch(Exception ex){updateTimer.Stop();AddLog("Update: "+ex.Message);}};
        websiteTimer.Tick+=async(_,_)=>await CheckWebsites();
        Loaded+=async(_,_)=>{await Refresh();if(!Environment.GetCommandLineArgs().Contains("--preview")){websiteTimer.Start();_=CheckWebsites();await CheckUpdatesOnStartup();}};
        Closed+=(_,_)=>{updateTimer.Stop();websiteTimer.Stop();WebsiteMonitor.Cancel();ReleasePreparedImage();};
    }
    void AddLog(string text)=>Dispatcher.Invoke(()=>{Log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");Log.ScrollToEnd();});
    async void ModeChanged(object sender,RoutedEventArgs e)
    {
        if(!IsLoaded||selectingSource)return;
        ReleasePreparedImage();
        ConfigureMode();selectedImage=null;Windows11Compatibility.IsChecked=false;ImagePath.Clear();ImageInfo.Text="Noch keine Quelle ausgewählt.";UpdateBootInfo();await Refresh();
    }
    void ConfigureMode()
    {
        bool win=WindowsMode.IsChecked==true;
        Heading.Text=win?"Windows-USB erstellen":"Linux / IMG auf USB schreiben";
        ModeHint.Text=win?"Windows-ISO auswählen oder herunterladen und das Boot-Verfahren passend zum PC wählen.":"Hybrid-ISO oder bootfähiges IMG auf USB schreiben. IMG.XZ wird vor dem Schreiben entpackt.";
        Notice.Text="Alle Partitionen und Dateien des USB-Ziels werden gelöscht. Vor dem Start Modell, Größe und Datenträgernummer prüfen.";
        Verify.Visibility=Visibility.Visible;GetIsoButton.Visibility=Visibility.Visible;
        GetIsoButton.Content=win?"Windows-ISO von Microsoft holen …":"Aktuelle Linux-ISO holen …";
        TargetHint.Text="Nur USB-Datenträger; System-, Start- und schreibgeschützte Laufwerke sind gesperrt.";
        BootOptions.Visibility=win?Visibility.Visible:Visibility.Collapsed;BootInfo.Visibility=Visibility.Visible;
        CompatibilityOptions.Visibility=win?Visibility.Visible:Visibility.Collapsed;
    }
    void CompatibilityChanged(object sender,RoutedEventArgs e){if(IsLoaded)UpdateBootInfo();}
    void BootChanged(object sender,SelectionChangedEventArgs e){if(IsLoaded)UpdateBootInfo();}
    void UpdateBootInfo()
    {
        Windows11Compatibility.IsEnabled=selectedImage is {Windows:true,Windows11:true,Architecture:"amd64" or "arm64"};
        if(!Windows11Compatibility.IsEnabled&&Windows11Compatibility.IsChecked==true)Windows11Compatibility.IsChecked=false;
        try{BootInfo.Text=selectedImage is null?"Das Abbild wird nach der Auswahl schreibgeschützt untersucht. Vor dem Schreiben erscheint eine Zusammenfassung.":UsbPlan.Create(selectedImage,selectedImage.Windows?SelectedBoot:"Image",Windows11Compatibility.IsChecked==true).Description;}
        catch(Exception ex){BootInfo.Text=ex.Message;}
    }
    public async Task SelectImage(string path)
    {
        if(busy)return;
        ReleasePreparedImage();
        SetBusy(true);selectedImage=null;Windows11Compatibility.IsChecked=false;Windows11Compatibility.IsEnabled=false;ImagePath.Clear();ImageInfo.Text="Abbild wird schreibgeschützt untersucht …";
        try
        {
            path=Path.GetFullPath(path);
            if(path.EndsWith(".img.xz",StringComparison.OrdinalIgnoreCase))
            {
                ImageInfo.Text="IMG.XZ wird entpackt und geprüft …";
                preparedImage=await Task.Run(()=>CompressedImage.Expand(path));
            }
            var image=(await UsbImages.Inspect(preparedImage?.Path??path)) with { Path=path };
            if(!image.Windows&&!image.Hybrid)throw new IOException(image.Description);
            selectingSource=true;
            try{if(image.Windows)WindowsMode.IsChecked=true;else RawMode.IsChecked=true;ConfigureMode();BootChoice.SelectedIndex=image.Efi?0:1;}
            finally{selectingSource=false;}
            selectedImage=image;ImagePath.Text=image.Path;ImageInfo.Text=$"{image.Bytes/1073741824.0:F2} GiB"+(preparedImage is null?"":" entpackt")+$" · {image.Description}"+(image.Windows11?$" · Windows 11 ({image.Architecture})":"");
            Targets.SelectedIndex=-1;UpdateBootInfo();AddLog("Abbild erkannt: "+image.Description);
        }
        catch(Exception ex){ReleasePreparedImage();ImageInfo.Text="Abbild nicht ausgewählt: "+ex.Message;AddLog(ex.Message);}
        finally{SetBusy(false);Progress.IsIndeterminate=false;Status.Text="Bitte Ziel auswählen";UpdateBootInfo();}
    }
    void ReleasePreparedImage()
    {
        preparedImage?.Dispose();
        preparedImage=null;
    }
    async void Browse(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="Abbilder (*.iso;*.img;*.img.xz)|*.iso;*.img;*.img.xz",CheckFileExists=true};
        if(dialog.ShowDialog()==true)await SelectImage(dialog.FileName);
    }
    async void RefreshClick(object sender,RoutedEventArgs e)=>await Refresh();
    async Task Refresh()
    {
        if(busy)return;
        Settings.IsEnabled=false;Navigation.IsEnabled=false;StartButton.IsEnabled=false;
        try{Targets.ItemsSource=null;var targets=await Task.Run(Backend.ListUsb);Targets.ItemsSource=targets;Status.Text=targets.Count==0?"Kein geeigneter USB-Stick gefunden":"Bitte Ziel auswählen";}
        catch(Exception ex){AddLog(ex.Message);Status.Text="USB-Erkennung fehlgeschlagen";}
        finally{Settings.IsEnabled=true;Navigation.IsEnabled=true;StartButton.IsEnabled=true;}
    }
    void SetBusy(bool value)
    {
        busy=value;Settings.IsEnabled=!value;Navigation.IsEnabled=!value;StartButton.IsEnabled=!value;
        Progress.IsIndeterminate=value;if(value){Progress.Value=0;Status.Text="Vorgang läuft …";}
    }
    async void Start(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        bool win=WindowsMode.IsChecked==true,verify=Verify.IsChecked==true;string path=ImagePath.Text;
        string writePath=preparedImage?.Path??path;
        if(!File.Exists(path)){MessageBox.Show(this,"Bitte zuerst eine gültige ISO- oder IMG-Datei auswählen.");return;}
        if(Targets.SelectedItem is not Target target){MessageBox.Show(this,"Bitte einen USB-Stick auswählen.");return;}
        UsbPlan plan;
        SetBusy(true);
        try
        {
            var image=(await UsbImages.Inspect(writePath)) with { Path=path };
            if(image.Windows!=win)throw new IOException("Der Abbildtyp passt nicht mehr zum gewählten Modus. Bitte die Quelle erneut auswählen.");
            plan=UsbPlan.Create(image,win?SelectedBoot:"Image",Windows11Compatibility.IsChecked==true);
            if(image.Bytes>target.Size)throw new IOException("Das Abbild ist größer als der ausgewählte USB-Datenträger.");
        }
        catch(Exception ex){Status.Text="USB-Auftrag nicht gestartet";MessageBox.Show(this,ex.Message,"USB-Auftrag nicht gestartet");return;}
        finally{SetBusy(false);Progress.IsIndeterminate=false;}
        if(!ConfirmUsb(target,plan.Summary(target,verify),"Löschen und schreiben"))return;
        SetBusy(true);
        try
        {
            AddLog($"Start: {path} → {target.Label}");await Backend.WriteUsb(writePath,target,win,verify,AddLog,plan.BootMode,plan.Windows11Compatibility,preparedImage is null?null:path);
            Progress.IsIndeterminate=false;Progress.Value=100;Status.Text="Abgeschlossen";
            AddLog(verify?"USB-Stick geschrieben und Datenprüfung erfolgreich.":"USB-Stick geschrieben; Datenprüfung abgewählt.");
            MessageBox.Show(this,"Der USB-Stick wurde erfolgreich erstellt.","Startwerk");
        }
        catch(Exception ex){Status.Text="Fehlgeschlagen";AddLog("FEHLER: "+ex.Message);MessageBox.Show(this,ex.Message+"\n\nNach einem Schreibfehler kann der USB-Stick unvollständig sein.","USB-Vorgang fehlgeschlagen",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{SetBusy(false);}
    }

    bool ConfirmUsb(Target target, string summary, string action)
    {
        var dialog = new Window { Owner = this, Title = "USB-Auftrag prüfen und Ziel löschen", Width = 650, SizeToContent=SizeToContent.Height, MaxHeight=700, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = summary+$"\n\nZum Bestätigen eingeben: LÖSCHEN {target.Number}", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Black });
        var input = new TextBox { Margin = new Thickness(0, 16, 0, 16) }; panel.Children.Add(input);
        var button = new Button { Content = action, IsEnabled = false };
        input.TextChanged += (_, _) => button.IsEnabled = input.Text == $"LÖSCHEN {target.Number}";
        button.Click += (_, _) => dialog.DialogResult = true; panel.Children.Add(button);
        dialog.Content = new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}; return dialog.ShowDialog() == true;
    }
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (busy&&!InstallingUpdate) { e.Cancel = true; MessageBox.Show(this, "Bitte warte, bis der Vorgang beendet ist.", "Vorgang läuft"); }
    }
}
