using System.Windows;

namespace Startwerk;
public sealed record WebsiteRow(string Name,string Status,string Checked,string Address,string Message,string State);
public partial class WebsiteWindow:Window
{
    const string Explanation="Fremde Weiterleitungen und Zertifikatsfehler bleiben gesperrt. Die Prüfung bestätigt Domain und HTTPS, nicht sämtliche Inhalte einer Webseite.";
    public WebsiteWindow(Window owner)
    {
        InitializeComponent();Owner=owner;Resources.MergedDictionaries.Add(owner.Resources);
        ShowReport(WebsiteSafety.Load(WebsiteMonitor.ReportPath));Loaded+=async(_,_)=>await Refresh(false);
    }
    void ShowReport(WebsiteReport? report)
    {
        Rows.ItemsSource=report?.Results.Select(r=>new WebsiteRow(r.Name,r.State switch{"Verified"=>r.Repaired?"Wiederhergestellt":"Adresse geprüft","Restricted"=>"Anfrage abgelehnt","Blocked"=>"Gesperrt",_=>"Nicht bestätigt"},r.CheckedUtc.ToLocalTime().ToString("dd.MM. HH:mm"),r.FinalAddress??r.Address,r.Message,r.State)).ToArray();
        Schedule.Text=report==null?"Noch kein gültiges Prüfergebnis gespeichert.":$"Letzte Prüfung: {report.CompletedUtc.ToLocalTime():dd.MM. HH:mm} · Nächste: {(report.CompletedUtc+WebsiteSafety.Interval).ToLocalTime():dd.MM. HH:mm}";
    }
    void ShowSelected(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(Details==null)return;Details.Text=Rows.SelectedItem is WebsiteRow row?$"{row.Name}: {row.Message}\n{row.Address}\n{Explanation}":Explanation;
    }
    async void RefreshClick(object sender,RoutedEventArgs e)=>await Refresh(true);
    public async Task Refresh(bool force)
    {
        CheckNow.IsEnabled=false;Schedule.Text="Adressen, HTTPS und Weiterleitungen prüfen …";
        try{ShowReport(await WebsiteMonitor.Refresh(force));}
        catch(OperationCanceledException){Schedule.Text="Prüfung beendet.";}
        catch(Exception ex){Schedule.Text="Prüfergebnis nicht verfügbar: "+ex.Message;}
        finally{CheckNow.IsEnabled=true;}
    }
}
