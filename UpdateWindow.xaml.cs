using System.Windows;

namespace Startwerk;
public partial class UpdateWindow : Window
{
    readonly MainWindow parent;
    UpdateSettings settings;
    AvailableUpdate? available;
    bool working;
    public UpdateWindow(MainWindow owner)
    {
        InitializeComponent();Owner=owner;parent=owner;Resources.MergedDictionaries.Add(owner.Resources);
        settings=Updates.LoadSettings();Repository.Text=settings.Repository;AutoCheck.IsChecked=settings.AutoCheck;AutoInstall.IsChecked=settings.AutoInstall;
        VersionLabel.Text="Installiert: "+Updates.VersionText;Status.Text="Update-Kanal: GitHub Releases";
        Repository.TextChanged+=(_,_)=>{available=null;InstallButton.IsEnabled=false;};
    }
    void AddLog(string message)=>Dispatcher.Invoke(()=>{Log.AppendText(message+Environment.NewLine);Log.ScrollToEnd();});
    void SetWorking(bool value){working=value;SettingsPanel.IsEnabled=!value;SaveButton.IsEnabled=!value;CheckButton.IsEnabled=!value;InstallButton.IsEnabled=!value&&available!=null;Progress.IsIndeterminate=value;}
    void Store()
    {
        string repository=Repository.Text.Trim();if(repository.StartsWith("https://github.com/",StringComparison.OrdinalIgnoreCase))repository=repository[19..].TrimEnd('/');
        if(!string.Equals(settings.Repository,repository,StringComparison.OrdinalIgnoreCase))settings.LastCheckUtc=default;
        settings.Repository=repository;settings.AutoCheck=AutoCheck.IsChecked==true;settings.AutoInstall=AutoInstall.IsChecked==true;Updates.SaveSettings(settings);Repository.Text=repository;
    }
    void Save(object sender,RoutedEventArgs e){try{Store();available=null;InstallButton.IsEnabled=false;Status.Text="Update-Einstellungen gespeichert.";}catch(Exception ex){Status.Text=ex.Message;}}
    async void Check(object sender,RoutedEventArgs e)
    {
        if(working)return;
        try
        {
            Store();available=null;SetWorking(true);Status.Text="GitHub wird geprüft …";
            available=await Updates.Check(settings.Repository);settings.LastCheckUtc=DateTime.UtcNow;Updates.SaveSettings(settings);
            Status.Text=available==null?"Du verwendest die aktuelle Version.":"Version "+available.Payload.Version+" ist verfügbar.";
            if(available!=null)AddLog(available.Payload.Notes);
        }
        catch(Exception ex){Status.Text="Update-Prüfung nicht möglich";AddLog(ex.Message);}
        finally{SetWorking(false);}
    }
    async void Install(object sender,RoutedEventArgs e)
    {
        if(working||available==null)return;
        try
        {
            SetWorking(true);var prepared=await Updates.Download(available,AddLog);Status.Text="Update wird installiert …";
            parent.InstallPreparedUpdate(prepared);
        }
        catch(Exception ex){AddLog(ex.Message);Status.Text="Update nicht installiert";}
        finally{SetWorking(false);}
    }
    void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e){if(working&&!parent.InstallingUpdate){e.Cancel=true;Status.Text="Bitte warten, bis der Update-Vorgang beendet ist.";}}
}
