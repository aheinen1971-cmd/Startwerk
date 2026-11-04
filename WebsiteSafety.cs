using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Startwerk;
public sealed record WebsiteResult(string Provider,string Name,string Address,string? FinalAddress,DateTime CheckedUtc,string State,string Message,bool Repaired=false)
{
    public bool CanOpen => State is "Verified" or "Restricted";
}
public sealed record WebsiteReport(DateTime CompletedUtc,WebsiteResult[] Results);
public static class WebsiteSafety
{
    public static readonly TimeSpan Interval=TimeSpan.FromHours(6);
    public static bool Https(Uri uri)=>uri.IsAbsoluteUri&&uri.Scheme=="https"&&uri.IsDefaultPort&&string.IsNullOrEmpty(uri.UserInfo)&&uri.HostNameType==UriHostNameType.Dns&&!uri.OriginalString.Any(char.IsControl);
    public static bool MicrosoftPage(Uri uri)=>Https(uri)&&(uri.IdnHost is "www.microsoft.com" or "microsoft.com")&&
        (uri.AbsolutePath.TrimEnd('/') is "/de-de/software-download/windows11" or "/de-de/software-download/windows11arm64")&&string.IsNullOrEmpty(uri.Query);
    public static bool Page(string provider,Uri uri)
    {
        if(provider=="Windows")return MicrosoftPage(uri);
        var alternative=LinuxCatalog.Alternatives.SingleOrDefault(x=>x.Id==provider);
        if(alternative!=null)return LinuxCatalog.Matches(alternative,uri);
        var official=LinuxCatalog.Items.SingleOrDefault(x=>x.Id==provider);
        if(official==null||!Https(uri))return false;
        string host=new Uri(official.Page).IdnHost;
        string alias=host.StartsWith("www.",StringComparison.Ordinal)?host[4..]:"www."+host;
        return uri.IdnHost==host||uri.IdnHost==alias||LinuxCatalog.Alternatives.Any(x=>x.Provider==provider&&LinuxCatalog.Matches(x,uri));
    }
    public static string Start(string provider)=>provider=="Windows"?IsoDownloads.MicrosoftPage:LinuxCatalog.Alternatives.SingleOrDefault(x=>x.Id==provider)?.Page??LinuxCatalog.Items.Single(x=>x.Id==provider).Page;
    public static int SourceCount=>LinuxCatalog.Items.Count+LinuxCatalog.Alternatives.Count+1;
    public static bool Due(WebsiteReport? report,DateTime utc)=>report==null||report.CompletedUtc>utc||utc-report.CompletedUtc>=Interval;
    public static async Task<WebsiteResult> Check(string provider,string name,string address,CancellationToken token=default,HttpClient? transport=null)
    {
        if(!Uri.TryCreate(address,UriKind.Absolute,out var start)||!Page(provider,start))return new(provider,name,address,null,DateTime.UtcNow,"Blocked","Adresse gehört nicht zur freigegebenen Anbieter-Liste.");
        using var owned=transport==null?IsoDownloads.CreateClient():null;var client=transport??owned!;
        WebsiteResult? last=null;
        for(int attempt=0;attempt<2;attempt++)
        {
            last=null;
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(15));var uri=start;
            try
            {
                for(int redirects=0;redirects<6;redirects++)
                {
                    if(!Page(provider,uri))return new(provider,name,address,null,DateTime.UtcNow,"Blocked","Weiterleitung auf eine nicht freigegebene Adresse wurde vor dem Zugriff gesperrt.");
                    using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
                    if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        if(response.Headers.Location==null)return new(provider,name,address,null,DateTime.UtcNow,"Blocked","Ungültige Weiterleitung ohne Ziel.");
                        uri=new Uri(uri,response.Headers.Location);continue;
                    }
                    if(response.IsSuccessStatusCode)return new(provider,name,address,uri.AbsoluteUri,DateTime.UtcNow,"Verified","HTTPS und Anbieteradresse geprüft.",attempt>0);
                    if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                        return new(provider,name,address,uri.AbsoluteUri,DateTime.UtcNow,"Restricted","Anbieteradresse und HTTPS bestätigt; automatische Abfrage wird vom Anbieter abgelehnt.");
                    last=new(provider,name,address,uri.AbsoluteUri,DateTime.UtcNow,"Unavailable","Anbieter meldet HTTP "+(int)response.StatusCode+". Kein geprüfter Seitenaufruf möglich.");break;
                }
                last??=new(provider,name,address,null,DateTime.UtcNow,"Blocked","Zu viele Weiterleitungen.");
                if(last.State=="Blocked")return last;
            }
            catch(OperationCanceledException) when(!token.IsCancellationRequested){last=new(provider,name,address,null,DateTime.UtcNow,"Unavailable","Zeitüberschreitung; Originaladresse wird erneut geprüft.");}
            catch(HttpRequestException){last=new(provider,name,address,null,DateTime.UtcNow,"Unavailable","HTTPS-Verbindung nicht bestätigt. Netzwerk-, Zertifikats- oder Anbieterfehler; keine Ausnahme zugelassen.");}
            if(attempt==0)await Task.Delay(300,token);
        }
        return last! with{Message=last.Message+" Automatische Wiederholungsprüfung ebenfalls ohne Erfolg."};
    }
    public static async Task<WebsiteReport> CheckAll(CancellationToken token=default)
    {
        var targets=new[]{("Windows","Microsoft",IsoDownloads.MicrosoftPage)}.Concat(LinuxCatalog.Items.Select(x=>(x.Id,x.Name,x.Page))).Concat(LinuxCatalog.Alternatives.Select(x=>(x.Id,x.Name,x.Page)));
        using var concurrency=new SemaphoreSlim(3);
        var results=await Task.WhenAll(targets.Select(async item=>
        {
            await concurrency.WaitAsync(token);try{return await Check(item.Item1,item.Item2,item.Item3,token);}finally{concurrency.Release();}
        }));
        return new(DateTime.UtcNow,results);
    }
    public static WebsiteReport? Load(string path)
    {
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>256*1024)return null;
            var report=JsonSerializer.Deserialize<WebsiteReport>(File.ReadAllText(path));
            if(report==null||report.Results==null||report.Results.Length!=SourceCount||report.CompletedUtc>DateTime.UtcNow.AddMinutes(1))return null;
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var result in report.Results)
                if(result==null||string.IsNullOrWhiteSpace(result.Provider)||string.IsNullOrWhiteSpace(result.Name)||!ids.Add(result.Provider)||result.Address!=Start(result.Provider)||!Uri.TryCreate(result.Address,UriKind.Absolute,out var start)||!Page(result.Provider,start)||
                   (result.FinalAddress!=null&&(!Uri.TryCreate(result.FinalAddress,UriKind.Absolute,out var end)||!Page(result.Provider,end)))||
                   result.State is not ("Verified" or "Restricted" or "Unavailable" or "Blocked"))return null;
            return report;
        }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){return null;}
    }
    public static void Save(string path,WebsiteReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        for(string? directory=Path.GetDirectoryName(Path.GetFullPath(path));directory!=null;directory=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)))
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Umgeleiteter Speicherordner für Webseitenprüfungen ist gesperrt.");
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Umgeleitete Prüfdatei ist gesperrt.");
        try{File.WriteAllText(temp,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
