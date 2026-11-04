using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Startwerk;
public sealed record IsoSource(string Provider,string Title,string Version,Uri Url,string FileName,string? Sha256,long? Size);
public sealed record IsoDownloadResult(string Path,string Sha256,bool PublisherHashVerified,string Provider);
public static class IsoDownloads
{
    public const string MicrosoftPage="https://www.microsoft.com/de-de/software-download/windows11";
    public const long MaximumSize=32L*1024*1024*1024;
    public static bool TrustedUri(string provider,Uri uri)
    {
        if(!WebsiteSafety.Https(uri))return false;
        string host=uri.IdnHost;
        return provider switch
        {
            "Windows"=>host is "software.download.prss.microsoft.com" or "software-download.microsoft.com" or "download.microsoft.com",
            "Ubuntu"=>host=="releases.ubuntu.com",
            "Debian"=>host=="cdimage.debian.org"||host=="ftp.acc.umu.se"||host.EndsWith(".ftp.acc.umu.se",StringComparison.OrdinalIgnoreCase),
            "Fedora"=>host is "fedoraproject.org" or "download.fedoraproject.org" or "dl.fedoraproject.org",
            "Mint" or "MintMate" or "MintXfce"=>host is "linuxmint.com" or "www.linuxmint.com" or "pub.linuxmint.io" or "mirrors.kernel.org",
            _=>false
        };
    }
    public static bool MicrosoftPageUri(Uri uri)=>WebsiteSafety.MicrosoftPage(uri);
    public static IsoSource MicrosoftSource(string url,string? expectedHash)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||!TrustedUri("Windows",uri)||!uri.AbsolutePath.EndsWith(".iso",StringComparison.OrdinalIgnoreCase))throw new IOException("Bitte einen offiziellen Microsoft-ISO-Link verwenden. Installationsassistenten und EXE-Dateien werden hier nicht ausgeführt.");
        string name=Uri.UnescapeDataString(uri.Segments[^1]);
        if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name.Contains('/')||name.Contains('\\'))throw new IOException("Ungültiger ISO-Dateiname.");
        string hash=(expectedHash??"").Trim().Replace(" ","");
        if(hash.Length!=0&&!Regex.IsMatch(hash,@"^[a-fA-F0-9]{64}$"))throw new IOException("Die Microsoft-Prüfsumme muss aus 64 SHA-256-Zeichen bestehen.");
        return new("Windows",name,"Microsoft-Auswahl",uri,name,hash.Length==0?null:hash.ToUpperInvariant(),null);
    }
    public static HttpClient CreateClient()
    {
        var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Startwerk/"+Updates.VersionText);return client;
    }
    static async Task<HttpResponseMessage> Fetch(HttpClient client,string provider,Uri uri,CancellationToken token)
    {
        for(int i=0;i<6;i++)
        {
            if(!TrustedUri(provider,uri))throw new IOException("Die ISO-Quelle oder Weiterleitung gehört nicht zum gewählten Anbieter.");
            var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next=response.Headers.Location;response.Dispose();if(next==null)throw new IOException("Leere Download-Weiterleitung.");uri=new Uri(uri,next);continue;
            }
            return response;
        }
        throw new IOException("Zu viele ISO-Weiterleitungen.");
    }
    static async Task<string> Text(HttpClient client,string provider,string uri,CancellationToken token)
    {
        using var response=await Fetch(client,provider,new Uri(uri),token);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>4*1024*1024)throw new IOException("Release-Verzeichnis ist zu groß.");
        await using var stream=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buffer=new byte[16384];
        while(true){int read=await stream.ReadAsync(buffer,token);if(read==0)break;if(memory.Length+read>4*1024*1024)throw new IOException("Release-Verzeichnis ist zu groß.");memory.Write(buffer,0,read);}return Encoding.UTF8.GetString(memory.ToArray());
    }
    public static string Checksum(string text,string fileName)
    {
        foreach(string line in text.Split('\n'))
        {
            var match=Regex.Match(line.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$");
            if(match.Success&&match.Groups[2].Value==fileName)return match.Groups[1].Value.ToUpperInvariant();
        }
        throw new IOException("Für dieses ISO wurde keine offizielle SHA-256-Prüfsumme gefunden.");
    }
    public static string UbuntuVersion(string listing)
    {
        var versions=Regex.Matches(listing,"href=[\"'](?<v>[0-9]{2}\\.04)/[\"']",RegexOptions.IgnoreCase)
            .Select(m=>Version.Parse(m.Groups["v"].Value)).Where(v=>v.Major%2==0&&v.Major<=DateTime.UtcNow.Year-2000).OrderDescending().ToArray();
        if(versions.Length==0)throw new IOException("Keine aktuelle Ubuntu-LTS-Veröffentlichung gefunden.");return $"{versions[0].Major}.{versions[0].Minor:00}";
    }
    public static string IsoName(string listing,string provider)
    {
        string pattern=provider=="Ubuntu"?@"^ubuntu-(?<v>[0-9]+\.[0-9]+(?:\.[0-9]+)*)-desktop-amd64\.iso$":@"^debian-(?<v>[0-9]+\.[0-9]+(?:\.[0-9]+)*)-amd64-netinst\.iso$";
        var names=Regex.Matches(listing,"href=[\"']([^\"']+)[\"']",RegexOptions.IgnoreCase).Select(m=>m.Groups[1].Value).Distinct()
            .Select(name=>(Name:name,Match:Regex.Match(name,pattern))).Where(x=>x.Match.Success).OrderByDescending(x=>Version.Parse(x.Match.Groups["v"].Value)).ToArray();
        if(names.Length==0)throw new IOException("Keine passende 64-Bit-ISO im offiziellen Verzeichnis gefunden.");return names[0].Name;
    }
    public static IsoSource FedoraSource(string json)
    {
        using var document=JsonDocument.Parse(json);
        var item=document.RootElement.EnumerateArray().Where(x=>x.GetProperty("arch").GetString()=="x86_64"&&x.GetProperty("variant").GetString()=="Workstation"&&Regex.IsMatch(x.GetProperty("version").GetString()??"",@"^[0-9]+$"))
            .OrderByDescending(x=>int.Parse(x.GetProperty("version").GetString()!)).FirstOrDefault();
        if(item.ValueKind==JsonValueKind.Undefined)throw new IOException("Kein stabiles Fedora-Workstation-ISO gefunden.");
        var original=new Uri(item.GetProperty("link").GetString()!);
        if(!TrustedUri("Fedora",original)||!original.AbsolutePath.EndsWith(".iso"))throw new IOException("Fedora meldet eine unerwartete Downloadquelle.");
        // The canonical Fedora server avoids redirection to arbitrary third-party mirrors.
        var url=new UriBuilder(original){Host="dl.fedoraproject.org"}.Uri;
        string hash=item.GetProperty("sha256").GetString()??"";
        if(!Regex.IsMatch(hash,@"^[a-fA-F0-9]{64}$"))throw new IOException("Fedora-Prüfsumme fehlt.");
        long size=item.GetProperty("size").ValueKind==JsonValueKind.String?long.Parse(item.GetProperty("size").GetString()!):item.GetProperty("size").GetInt64();
        return new("Fedora","Fedora Workstation "+item.GetProperty("version").GetString()+" · x86_64",item.GetProperty("version").GetString()!,url,Path.GetFileName(url.AbsolutePath),hash.ToUpperInvariant(),size);
    }
    public static async Task<IsoSource> LatestLinux(string provider,CancellationToken token=default,HttpClient? transport=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var owned=transport==null?CreateClient():null;var client=transport??owned!;
        if(provider is "Mint" or "MintMate" or "MintXfce")
        {
            string edition=provider=="MintMate"?"MATE":provider=="MintXfce"?"Xfce":"Cinnamon";
            string main=await Text(client,provider,"https://linuxmint.com/download.php",timeout.Token);
            string page=MintEdition(main,edition);
            var source=MintSource(await Text(client,provider,page,timeout.Token),provider,edition);
            string hashes=await Text(client,provider,new Uri(source.Url,"sha256sum.txt").AbsoluteUri,timeout.Token);
            return source with{Sha256=Checksum(hashes,source.FileName)};
        }
        if(provider=="Fedora")return FedoraSource(await Text(client,provider,"https://fedoraproject.org/releases.json",timeout.Token));
        string folder,title;
        if(provider=="Ubuntu")
        {
            string version=UbuntuVersion(await Text(client,provider,"https://releases.ubuntu.com/",timeout.Token));
            folder="https://releases.ubuntu.com/"+version+"/";title="Ubuntu Desktop LTS · amd64";
        }
        else if(provider=="Debian"){folder="https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/";title="Debian Netinstall · amd64";}
        else throw new IOException("Diese Linux-Distribution ist noch nicht im Katalog.");
        string name=IsoName(await Text(client,provider,folder,timeout.Token),provider);
        string checksum=Checksum(await Text(client,provider,folder+"SHA256SUMS",timeout.Token),name);
        return new(provider,title,Regex.Match(name,@"[0-9]+\.[0-9]+(?:\.[0-9]+)*").Value,new Uri(folder+name),name,checksum,null);
    }
    public static string MintEdition(string html,string edition)
    {
        if(edition is not "Cinnamon" and not "MATE" and not "Xfce")throw new IOException("Unbekannte Mint-Ausgabe.");
        var match=Regex.Match(html,Regex.Escape(edition)+@" Edition[\s\S]*?href=[""'](?<page>edition\.php\?id=[0-9]+)[""']",RegexOptions.IgnoreCase);
        if(!match.Success)throw new IOException("Die aktuelle Mint-Ausgabe konnte nicht eindeutig gefunden werden.");
        return "https://linuxmint.com/"+match.Groups["page"].Value;
    }
    public static IsoSource MintSource(string html,string provider,string edition)
    {
        string flavor=edition.ToLowerInvariant();
        var match=Regex.Match(html,@"https://pub\.linuxmint\.io/stable/(?<version>[0-9]+\.[0-9]+)/(?<name>linuxmint-\k<version>-"+Regex.Escape(flavor)+@"-64bit\.iso)");
        if(!match.Success)throw new IOException("Kein eindeutiges stabiles Mint-ISO auf dem offiziell verlinkten Server gefunden.");
        return new(provider,"Linux Mint "+edition+" · Intel/AMD 64 Bit",match.Groups["version"].Value,new Uri(match.Value),match.Groups["name"].Value,null,null);
    }
    public static void ValidateIsoFile(string path)
    {
        using var stream=File.OpenRead(path);
        if(stream.Length<65536||stream.Length%2048!=0)throw new IOException("Die heruntergeladene Datei ist kein vollständiges ISO-Abbild.");
        stream.Position=32768;var descriptors=new byte[32768];stream.ReadExactly(descriptors);
        string text=Encoding.ASCII.GetString(descriptors);
        if(!text.Contains("CD001",StringComparison.Ordinal)&&!text.Contains("NSR02",StringComparison.Ordinal)&&!text.Contains("NSR03",StringComparison.Ordinal))throw new IOException("In der Datei wurde kein ISO-/UDF-Dateisystem erkannt.");
    }
    public static async Task<IsoDownloadResult> Download(IsoSource source,string destination,Action<string> log,Action<double> progress,CancellationToken token=default,HttpClient? transport=null)
    {
        if(!TrustedUri(source.Provider,source.Url)||!source.Url.AbsolutePath.EndsWith(".iso",StringComparison.OrdinalIgnoreCase))throw new IOException("Nicht unterstützte ISO-Quelle.");
        if(source.Provider!="Windows"&&(source.Sha256==null||!Regex.IsMatch(source.Sha256,@"^[a-fA-F0-9]{64}$")))throw new IOException("Offizielle Linux-Prüfsumme fehlt.");
        if(source.Size is <=0 or >MaximumSize)throw new IOException("Ungültige ISO-Größe.");
        destination=Path.GetFullPath(destination);
        if(!Path.GetExtension(destination).Equals(".iso",StringComparison.OrdinalIgnoreCase))throw new IOException("Bitte als ISO-Datei speichern.");
        for(string? item=Path.GetDirectoryName(destination);item!=null;item=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item)))if(Directory.Exists(item)&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new IOException("Umgeleitete Download-Zielordner werden nicht unterstützt.");
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromHours(8));
            using var owned=transport==null?CreateClient():null;var client=transport??owned!;
            using var response=await Fetch(client,source.Provider,source.Url,deadline.Token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json")throw new IOException("Der Anbieter hat eine Webseite statt einer ISO geliefert. Bei Microsoft bitte einen frischen Downloadlink erzeugen.");
            long? expected=source.Size??response.Content.Headers.ContentLength;
            if(expected is <=0 or >MaximumSize)throw new IOException("Der Anbieter meldet eine unerwartete ISO-Größe.");
            if(source.Size!=null&&response.Content.Headers.ContentLength is long supplied&&supplied!=source.Size)throw new IOException("ISO-Größe weicht vom offiziellen Releasekatalog ab.");
            var root=Path.GetPathRoot(destination)!;
            if(expected is long needed&&new DriveInfo(root).AvailableFreeSpace<needed+256L*1024*1024)throw new IOException("Zu wenig freier Speicher für die ISO und 256 MiB Reserve.");
            await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long total=0;int last=-1;var buffer=new byte[1024*1024];
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,buffer.Length,true))
            {
                while(true)
                {
                    int count=await input.ReadAsync(buffer,deadline.Token);if(count==0)break;total+=count;
                    if(total>MaximumSize||expected is long maximum&&total>maximum)throw new IOException("ISO-Download überschreitet die erwartete Größe.");
                    await output.WriteAsync(buffer.AsMemory(0,count),deadline.Token);hash.AppendData(buffer,0,count);
                    if(expected is long size){double percent=100.0*total/size;progress(percent);if((int)percent/10!=last){log($"ISO laden: {percent:F0} % · {total/1073741824.0:F2} GiB");last=(int)percent/10;}}
                    else if((int)(total/(128L*1024*1024))!=last){log($"ISO laden: {total/1073741824.0:F2} GiB");last=(int)(total/(128L*1024*1024));}
                }
                await output.FlushAsync(deadline.Token);
            }
            if(expected is long complete&&total!=complete)throw new IOException("Der ISO-Download ist unvollständig.");
            string digest=Convert.ToHexString(hash.GetHashAndReset());
            if(source.Sha256!=null&&!digest.Equals(source.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Die ISO stimmt nicht mit der offiziellen SHA-256-Prüfsumme überein.");
            ValidateIsoFile(temporary);token.ThrowIfCancellationRequested();File.Move(temporary,destination,true);
            log(source.Sha256!=null?"ISO vollständig geladen; offizielle SHA-256-Prüfsumme bestätigt.":"ISO vollständig geladen. SHA-256 berechnet; keine Microsoft-Vergleichsprüfsumme eingegeben.");
            return new(destination,digest,source.Sha256!=null,source.Provider);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
