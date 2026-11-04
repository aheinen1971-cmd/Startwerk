using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Startwerk;
public partial class IsoDownloadWindow : Window
{
    WebView2? browser;
    CancellationTokenSource? cancellation;
    IsoSource? available;
    bool working,initializing;
    bool alternative;
    MicrosoftWizard? windowsWizard;
    DateTime windowsPrepared;
    readonly TaskCompletionSource<bool> microsoftReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> MicrosoftReady=>microsoftReady.Task;
    public IsoDownloadResult? Result {get;private set;}
    LinuxDistribution? SelectedDistro=>Provider.SelectedItem as LinuxDistribution;
    string SelectedProvider=>SelectedDistro?.Id??"Windows";
    string MicrosoftPage=>MicrosoftWizard.Page(WindowsArchitecture.SelectedIndex==1);
    public IsoDownloadWindow(Window owner,bool windows)
    {
        InitializeComponent();Owner=owner;Resources.MergedDictionaries.Add(owner.Resources);
        foreach(var item in LinuxCatalog.Items)Provider.Items.Add(item);
        Provider.SelectedIndex=windows?0:1;
        MicrosoftLink.TextChanged+=(_,_)=>UpdateDownloadButton();
        Loaded+=async(_,_)=>await Configure();Closed+=(_,_)=>{cancellation?.Cancel();browser?.Dispose();};
    }
    async void ProviderChanged(object sender,SelectionChangedEventArgs e){if(IsLoaded)await Configure();}
    async Task Configure()
    {
        available=null;DownloadButton.IsEnabled=false;Progress.Value=0;bool windows=Provider.SelectedIndex==0;
        WindowsOptions.Visibility=windows?Visibility.Visible:Visibility.Collapsed;BrowserBorder.Visibility=windows&&alternative?Visibility.Visible:Visibility.Hidden;WizardBorder.Visibility=windows&&!alternative?Visibility.Visible:Visibility.Collapsed;LinuxInfo.Visibility=windows?Visibility.Collapsed:Visibility.Visible;
        FindButton.Visibility=windows?Visibility.Collapsed:Visibility.Visible;ReleaseInfo.Text="Oben auf „Aktuelle Version suchen“ klicken.";SourceInfo.Text="";
        var alternatives=LinuxCatalog.Alternatives.Where(x=>x.Provider==SelectedProvider).ToArray();AlternativeSource.ItemsSource=alternatives;AlternativeSource.SelectedIndex=alternatives.Length>0?0:-1;AlternativeSources.Visibility=!windows&&alternatives.Length>0?Visibility.Visible:Visibility.Collapsed;
        Description.Text=windows?"Windows, Architektur, Edition und Sprache auswählen. Der Assistent verwendet das öffentliche Microsoft-Formular. Die Microsoft-Seite bleibt als Alternative erhalten.":SelectedDistro!.Edition+" · Die Quelle wird automatisch geprüft.";
        Status.Text=windows?"Microsoft-Auswahl öffnen …":"Bereit für die Versionssuche";
        if(windows)await EnsureMicrosoft();else await FindLinux();
    }
    async Task EnsureMicrosoft()
    {
        if(initializing){while(initializing)await Task.Delay(100);return;}
        if(browser?.CoreWebView2!=null)return;
        initializing=true;
        try
        {
            browser=new WebView2();BrowserSlot.Content=browser;
            string folder=Path.Combine(Updates.StateDirectory,"MicrosoftDownloadWebView");
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:folder);
            await browser.EnsureCoreWebView2Async(environment);
            windowsWizard=new MicrosoftWizard(s=>browser.CoreWebView2.ExecuteScriptAsync(s),()=>browser.CoreWebView2.Source);
            browser.CoreWebView2.Settings.AreDevToolsEnabled=false;browser.CoreWebView2.Settings.IsStatusBarEnabled=false;
            bool Allowed(Uri uri)=>WebsiteSafety.MicrosoftPage(uri)||(IsoDownloads.TrustedUri("Windows",uri)&&uri.AbsolutePath.EndsWith(".iso",StringComparison.OrdinalIgnoreCase));
            browser.CoreWebView2.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||!Allowed(uri)){e.Cancel=true;available=null;UpdateDownloadButton();Status.Text="Nicht freigegebene Webseite gesperrt.";}};
            browser.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&Allowed(uri))browser.CoreWebView2.Navigate(uri.AbsoluteUri);else Status.Text="Nicht freigegebenes Fenster gesperrt.";};
            browser.CoreWebView2.ServerCertificateErrorDetected+=(_,e)=>{e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;available=null;UpdateDownloadButton();Status.Text="HTTPS-Zertifikat nicht bestätigt; Zugriff gesperrt.";};
            browser.CoreWebView2.DownloadStarting+=(_,e)=>
            {
                string url=e.DownloadOperation.Uri;e.Cancel=true;e.Handled=true;
                Dispatcher.BeginInvoke(new Action(async()=>
                {
                    if(working||Provider.SelectedIndex!=0||!alternative)return;
                    try{var source=IsoDownloads.MicrosoftSource(url,ExpectedHash.Text);MicrosoftLink.Text=url;await SaveAndDownload(source);}
                    catch(Exception ex){Status.Text="Download nicht gestartet";AddLog(ex.Message);}
                }));
            };
            browser.CoreWebView2.NavigationCompleted+=(_,e)=>
            {
                bool success=e.IsSuccess&&e.HttpStatusCode>=200&&e.HttpStatusCode<400;
                microsoftReady.TrySetResult(success);
                if(!working&&Provider.SelectedIndex==0)Status.Text=success?alternative?"Edition und Sprache auf der Microsoft-Seite wählen.":"Bereit: Microsoft-Auswahl laden.":"Microsoft-Seite nicht erreichbar. Browser und Direktlink sind als Alternative verfügbar.";
            };
            browser.CoreWebView2.Navigate(MicrosoftPage);
        }
        catch(Exception ex){microsoftReady.TrySetResult(false);Status.Text="Microsoft-Seite im Browser öffnen und ISO-Link einfügen.";AddLog("Eingebaute Microsoft-Seite nicht verfügbar: "+ex.Message);}
        finally{initializing=false;}
    }
    void UpdateDownloadButton()=>DownloadButton.IsEnabled=!working&&(available!=null||Provider.SelectedIndex==0&&alternative&&!string.IsNullOrWhiteSpace(MicrosoftLink.Text));
    void ClearWindowsResult()
    {
        available=null;WindowsReview.Text="Auswahl geändert. Bitte den offiziellen Download neu vorbereiten.";UpdateDownloadButton();
    }
    void ArchitectureChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!IsLoaded)return;WindowsEdition.ItemsSource=null;WindowsEdition.IsEnabled=false;EditionChanged(sender,e);ClearWindowsResult();
    }
    void EditionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!IsLoaded)return;WindowsLanguage.ItemsSource=null;WindowsLanguage.IsEnabled=false;LanguageButton.IsEnabled=WindowsEdition.SelectedItem!=null;WindowsLinkButton.IsEnabled=false;ClearWindowsResult();
    }
    void LanguageChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!IsLoaded)return;WindowsLinkButton.IsEnabled=WindowsLanguage.SelectedItem!=null;ClearWindowsResult();
    }
    async void ShowMicrosoft(object sender,RoutedEventArgs e)
    {
        if(working)return;alternative=true;available=null;MicrosoftLink.Clear();ExpectedHash.Clear();AlternativeOptions.Visibility=Visibility.Visible;WizardBorder.Visibility=Visibility.Collapsed;BrowserBorder.Visibility=Visibility.Visible;UpdateDownloadButton();
        await WindowsAction(async token=>{Status.Text="Microsoft-Seite öffnen …";await NavigateMicrosoft(token);Status.Text="Edition und Sprache auf der Microsoft-Seite wählen.";});
    }
    void ShowWizard(object sender,RoutedEventArgs e)
    {
        if(working)return;alternative=false;AlternativeOptions.Visibility=Visibility.Collapsed;WizardBorder.Visibility=Visibility.Visible;BrowserBorder.Visibility=Visibility.Hidden;ArchitectureChanged(sender,new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent,Array.Empty<object>(),Array.Empty<object>()));UpdateDownloadButton();
    }
    async Task WindowsAction(Func<CancellationToken,Task> action)
    {
        if(working)return;cancellation=new CancellationTokenSource();SetWorking(true);
        try{await action(cancellation.Token);}
        catch(OperationCanceledException){Status.Text="Microsoft-Auswahl abgebrochen";}
        catch(Exception ex){available=null;WindowsReview.Text="Die automatische Vorbereitung war nicht erfolgreich. Bitte erneut versuchen oder oben die Microsoft-Seite als Alternative öffnen.";Status.Text="Microsoft-Seite als Alternative verwenden oder erneut versuchen.";AddLog(ex.Message);}
        finally{cancellation.Dispose();cancellation=null;SetWorking(false);}
    }
    async Task NavigateMicrosoft(CancellationToken token)
    {
        await EnsureMicrosoft();
        if(browser?.CoreWebView2==null||windowsWizard==null)throw new IOException("WebView2 ist nicht verfügbar. Die Microsoft-Seite kann im Browser geöffnet werden.");
        browser.CoreWebView2.Stop();var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);string page=MicrosoftPage;
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e){if(e.IsSuccess&&browser.CoreWebView2.Source==page)ready.TrySetResult(true);}
        browser.CoreWebView2.NavigationCompleted+=Completed;
        try{browser.CoreWebView2.Navigate(page);await ready.Task.WaitAsync(TimeSpan.FromSeconds(45),token);}
        finally{browser.CoreWebView2.NavigationCompleted-=Completed;}
    }
    async void LoadWindows(object sender,RoutedEventArgs e)=>await LoadWindowsSelections();
    public Task LoadWindowsSelections()=>WindowsAction(async token=>
    {
        available=null;WindowsEdition.ItemsSource=null;WindowsLanguage.ItemsSource=null;LanguageButton.IsEnabled=false;WindowsLinkButton.IsEnabled=false;
        Status.Text="Aktuelle Microsoft-Auswahl laden …";await NavigateMicrosoft(token);
        WindowsEdition.ItemsSource=await windowsWizard!.Options("product-edition",token);WindowsEdition.IsEnabled=true;WindowsEdition.SelectedIndex=0;
        WindowsReview.Text="Editionen frisch von Microsoft geladen. Jetzt die Sprachen laden.";Status.Text="Edition wählen und Sprachen laden.";
    });
    async void LoadLanguages(object sender,RoutedEventArgs e)=>await LoadWindowsLanguages();
    public Task LoadWindowsLanguages()=>WindowsAction(async token=>
    {
        available=null;WindowsLanguage.ItemsSource=null;Status.Text="Verfügbare Sprachen von Microsoft laden …";
        var edition=WindowsEdition.SelectedItem as MicrosoftOption??throw new IOException("Bitte zuerst eine Edition laden und wählen.");
        await windowsWizard!.Submit("product-edition","submit-product-edition",edition,token);
        var languages=await windowsWizard.Options("product-languages",token);WindowsLanguage.ItemsSource=languages;WindowsLanguage.IsEnabled=true;
        WindowsLanguage.SelectedItem=languages.FirstOrDefault(x=>x.Label=="Deutsch")??languages[0];Status.Text="Sprache wählen und Download vorbereiten.";
    });
    async void PrepareWindows(object sender,RoutedEventArgs e)=>await PrepareWindowsDownload();
    public Task PrepareWindowsDownload()=>WindowsAction(async token=>
    {
        available=null;Status.Text="Frischen ISO-Link und Microsoft-Prüfsumme abrufen …";
        var language=WindowsLanguage.SelectedItem as MicrosoftOption??throw new IOException("Bitte zuerst eine Sprache wählen.");
        // Exclude links already on the page so an old language cannot win the form-update race.
        var previousLinks=(await windowsWizard!.CurrentLinks(token)).ToHashSet(StringComparer.Ordinal);
        await windowsWizard.Submit("product-languages","submit-sku",language,token);available=await windowsWizard.Link(language.Label,token,previousLinks);windowsPrepared=DateTime.UtcNow;
        MicrosoftLink.Text=available.Url.AbsoluteUri;ExpectedHash.Text=available.Sha256;
        WindowsReview.Text=$"4 · Download prüfen\nBereit: {available.FileName}\nSprache: {language.Label} · {(WindowsArchitecture.SelectedIndex==1?"Arm64":"x64")}\nQuelle: {available.Url.Host}\nOffizielle SHA-256: {available.Sha256}\nDer zeitlich begrenzte Link wurde frisch erzeugt. Jetzt unten den Speicherort wählen.";
        await Dispatcher.InvokeAsync(()=>WindowsReview.BringIntoView(),System.Windows.Threading.DispatcherPriority.Loaded);
        Status.Text="Offizielle ISO mit Microsoft-Prüfsumme vorbereitet.";
    });
    public static async Task CheckOfficialPage(LinuxDistribution distro,CancellationToken token)
    {
        var result=await WebsiteSafety.Check(distro.Id,distro.Name,distro.Page,token);
        if(result.State!="Verified")throw new IOException(result.Message);
    }
    async void OpenDistroPage(object sender,RoutedEventArgs e)
    {
        if(SelectedDistro is not {} distro)return;
        await OpenCheckedPage(distro.Id,distro.Name,distro.Page);
    }
    void AlternativeSourceChanged(object sender,SelectionChangedEventArgs e)
    {
        if(AlternativeNote==null)return;AlternativeNote.Text=AlternativeSource.SelectedItem is LinuxAlternative item?item.Page+"\n"+item.Note+"\nDie Seite wird vor dem Öffnen geprüft; Auswahl und Download erfolgen im Browser.":"";
        if(IsLoaded&&!working&&SelectedDistro is {} distro&&AlternativeSource.SelectedItem is LinuxAlternative selected)
        {
            ReleaseInfo.Text=distro.Name+" · Alternative gewählt";SourceInfo.Text="Ausgewählte Alternative: "+selected.Page;Status.Text="Die ausgewählte Adresse wird vor dem Öffnen erneut geprüft.";
        }
    }
    async void OpenAlternativeSource(object sender,RoutedEventArgs e)
    {
        if(AlternativeSource.SelectedItem is LinuxAlternative item)await OpenCheckedPage(item.Id,item.Name,item.Page);
    }
    void AddLog(string text)=>Dispatcher.Invoke(()=>{Log.AppendText(text+Environment.NewLine);Log.ScrollToEnd();});
    void SetWorking(bool value)
    {
        working=value;Options.IsEnabled=!value;BrowserBorder.IsEnabled=!value;WizardBorder.IsEnabled=!value;DistroPageButton.IsEnabled=!value;AlternativeSources.IsEnabled=!value;CancelButton.IsEnabled=value;
        UpdateDownloadButton();
        Progress.IsIndeterminate=value;
    }
    async void FindLatest(object sender,RoutedEventArgs e)
    {
        await FindLinux();
    }
    async Task FindLinux()
    {
        if(working)return;cancellation=new CancellationTokenSource();SetWorking(true);Status.Text="Offiziellen Releasekatalog prüfen …";
        try
        {
            var distro=SelectedDistro??throw new IOException("Bitte eine Linux-Distribution auswählen.");
            if(!distro.Direct)
            {
                var primary=await WebsiteSafety.Check(distro.Id,distro.Name,distro.Page,cancellation.Token);
                if(primary.State!="Verified")
                {
                    AddLog(primary.Message);
                    foreach(var alternative in LinuxCatalog.Alternatives.Where(x=>x.Provider==distro.Id))
                    {
                        var check=await WebsiteSafety.Check(alternative.Id,alternative.Name,alternative.Page,cancellation.Token);
                        if(check.State!="Verified")continue;
                        AlternativeSource.SelectedItem=alternative;ReleaseInfo.Text=distro.Name+" · Alternative erreichbar";SourceInfo.Text="Originalseite: "+primary.Message+"\nGeprüfte Alternative: "+check.FinalAddress;
                        LinuxVerification.Text="Eine vom Anbieter verlinkte Alternative wurde gefunden. Ausgabe, Architektur und Prüfsumme auf der Downloadseite wählen. Startwerk startet keinen unbeaufsichtigten Download und keine Installation.";
                        Status.Text="Geprüfte Alternative wählen und im Browser öffnen.";return;
                    }
                    throw new IOException(primary.Message+" Keine automatisch bestätigte Alternative gefunden.");
                }
                ReleaseInfo.Text=distro.Name+" · offizielle Seite erreichbar";
                SourceInfo.Text=distro.Edition+"\n"+distro.Page;
                LinuxVerification.Text="Für diese Distribution wird noch kein eindeutiger Direktdownload ermittelt. Auf der offiziellen Seite Ausgabe und Architektur wählen und deren Prüfanleitung befolgen. Komprimierte Abbilder zuerst entpacken; die gespeicherte ISO/IMG anschließend im USB-Modus auswählen.";
                Status.Text="Offizielle Quelle geprüft; Downloadseite öffnen.";return;
            }
            available=await IsoDownloads.LatestLinux(distro.Id,cancellation.Token);
            ReleaseInfo.Text=available.Title+"\nVersion "+available.Version;
            SourceInfo.Text="Datei: "+available.FileName+"\nQuelle: "+available.Url.Host+"\nSHA-256: "+available.Sha256;
            LinuxVerification.Text="1 · Aktuelle ISO und veröffentlichte SHA-256 gefunden.\n2 · ISO herunterladen und Speicherort wählen.\n3 · Die vollständige Datei wird automatisch mit der Prüfsumme verglichen. USB-Schreiben bleibt ein eigener Schritt.";
            Status.Text="Aktuelle ISO gefunden. Speicherort auf dem PC wählen.";
        }
        catch(OperationCanceledException){Status.Text="Versionssuche abgebrochen";}
        catch(Exception ex){available=null;ReleaseInfo.Text="Automatische Prüfung nicht erfolgreich";SourceInfo.Text=SelectedDistro?.Page??"";LinuxVerification.Text="Die offizielle Downloadseite bleibt als Alternative verfügbar. Es wird kein ungeprüfter Direktlink verwendet.";Status.Text="Versionssuche fehlgeschlagen";AddLog(ex.Message);}
        finally{cancellation.Dispose();cancellation=null;SetWorking(false);}
    }
    async void DownloadClick(object sender,RoutedEventArgs e)
    {
        if(working)return;
        try
        {
            if(Provider.SelectedIndex==0&&!alternative&&DateTime.UtcNow-windowsPrepared>TimeSpan.FromHours(1))throw new IOException("Bitte den Windows-Download erneut vorbereiten, damit ein frischer Microsoft-Link verwendet wird.");
            var source=Provider.SelectedIndex==0&&alternative?IsoDownloads.MicrosoftSource(MicrosoftLink.Text,ExpectedHash.Text):available??throw new IOException("Bitte zuerst den Download vorbereiten.");await SaveAndDownload(source);
        }
        catch(Exception ex){Status.Text="Download nicht gestartet";AddLog(ex.Message);}
    }
    async Task SaveAndDownload(IsoSource source)
    {
        if(working)return;SetWorking(true);
        var dialog=new SaveFileDialog{Title="ISO auf dem PC speichern",Filter="ISO-Abbild|*.iso",FileName=source.FileName,OverwritePrompt=true,AddExtension=true};
        bool accepted;try{accepted=dialog.ShowDialog()==true;}catch{SetWorking(false);throw;}
        if(!accepted){SetWorking(false);return;}
        cancellation=new CancellationTokenSource();Status.Text="ISO wird heruntergeladen …";
        try
        {
            AddLog("Quelle: "+source.Url.GetLeftPart(UriPartial.Path));
            Result=await IsoDownloads.Download(source,dialog.FileName,AddLog,p=>Dispatcher.Invoke(()=>{Progress.IsIndeterminate=false;Progress.Value=p;}),cancellation.Token);
            SetWorking(false);DialogResult=true;
        }
        catch(OperationCanceledException){Status.Text="Download abgebrochen; unvollständige Datei verworfen.";}
        catch(Exception ex){Status.Text="ISO nicht übernommen";AddLog(ex.Message);}
        finally{cancellation.Dispose();cancellation=null;SetWorking(false);}
    }
    async void OpenMicrosoft(object sender,RoutedEventArgs e)
    {
        await OpenCheckedPage("Windows","Microsoft",MicrosoftPage);
    }
    async Task OpenCheckedPage(string provider,string name,string address)
    {
        if(working)return;cancellation=new CancellationTokenSource();SetWorking(true);Status.Text="Adresse und Weiterleitungen vor dem Öffnen prüfen …";
        try
        {
            var result=await WebsiteSafety.Check(provider,name,address,cancellation.Token);
            if(!result.CanOpen)throw new IOException(result.Message);
            Process.Start(new ProcessStartInfo(result.FinalAddress!){UseShellExecute=true})?.Dispose();
            Status.Text="Geprüfte Anbieteradresse geöffnet. Weitere Navigation wird vom externen Browser verwaltet.";
        }
        catch(OperationCanceledException){Status.Text="Seitenprüfung abgebrochen";}
        catch(Exception ex){Status.Text="Webseite nicht geöffnet";AddLog(ex.Message);}
        finally{cancellation.Dispose();cancellation=null;SetWorking(false);}
    }
    void Cancel(object sender,RoutedEventArgs e){cancellation?.Cancel();Status.Text="Wird abgebrochen …";}
    void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e){if(working){e.Cancel=true;cancellation?.Cancel();Status.Text="Download wird abgebrochen …";}}
}
