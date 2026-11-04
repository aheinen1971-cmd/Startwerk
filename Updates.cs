using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Startwerk;
public sealed record SignedUpdate(string Payload, string Signature);
public sealed record UpdatePayload(string Repository, string Version, string Tag, string Asset, string Sha256, long Size, string Notes);
public sealed record AvailableUpdate(SignedUpdate Envelope, UpdatePayload Payload);
public sealed record PreparedUpdate(AvailableUpdate Update, string Directory, string PayloadPath);
public sealed record InstallRequest(SignedUpdate Envelope, string Repository, string Target, string OldSha256, int ParentId, long ParentStartTicks);
public sealed class UpdateSettings
{
    public string Repository { get; set; } = "aheinen1971-cmd/Startwerk";
    public bool AutoCheck { get; set; } = true;
    public bool AutoInstall { get; set; }
    public DateTime LastCheckUtc { get; set; }
}
public static class Updates
{
    public static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Startwerk");
    public static string CacheDirectory => Path.Combine(StateDirectory, "Updates");
    public static Version CurrentVersion => Normalize(typeof(Updates).Assembly.GetName().Version!);
    public static string VersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";
    public static string PublicKey
    {
        get { using var stream=typeof(Updates).Assembly.GetManifestResourceStream("Startwerk.UpdatePublicKey") ?? throw new IOException("Update-Prüfschlüssel fehlt."); using var reader=new StreamReader(stream); return reader.ReadToEnd(); }
    }
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=true, WriteIndented=true };
    public static UpdateSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(Path.Combine(StateDirectory,"updates.json")),Json) ?? new(); }
        catch(IOException) { return new(); } catch(JsonException) { return new(); }
    }
    public static void SaveSettings(UpdateSettings settings)
    {
        ValidateRepository(settings.Repository); Directory.CreateDirectory(StateDirectory);
        string path=Path.Combine(StateDirectory,"updates.json"),temp=path+"."+Guid.NewGuid()+".tmp";
        try {File.WriteAllText(temp,JsonSerializer.Serialize(settings,Json));File.Move(temp,path,true);} finally {if(File.Exists(temp))File.Delete(temp);}
    }
    public static void ValidateRepository(string repository)
    {
        if(repository.Length>200 || !Regex.IsMatch(repository,@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$") || repository.Split('/')[1] is "." or "..") throw new IOException("GitHub-Projekt bitte als Benutzername/Projektname eingeben.");
    }
    public static Version Normalize(Version v) => new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));
    public static UpdatePayload Verify(SignedUpdate envelope,string repository,string? publicKey=null)
    {
        ValidateRepository(repository);
        if(envelope.Payload==null||envelope.Signature==null)throw new IOException("Update-Signatur fehlt.");
        if(envelope.Payload.Length>90000 || envelope.Signature.Length>2048)throw new IOException("Update-Beschreibung ist zu groß.");
        byte[] body,signature;
        try {body=Convert.FromBase64String(envelope.Payload);signature=Convert.FromBase64String(envelope.Signature);} catch(FormatException ex){throw new IOException("Ungültige Update-Signatur.",ex);}
        using var rsa=RSA.Create();rsa.ImportFromPem(publicKey??PublicKey);
        if(!rsa.VerifyData(body,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new IOException("Die Update-Signatur ist ungültig. Das Update wurde gesperrt.");
        var payload=JsonSerializer.Deserialize<UpdatePayload>(body,Json)??throw new IOException("Leere Update-Beschreibung.");
        if(!string.Equals(payload.Repository,repository,StringComparison.OrdinalIgnoreCase) || payload.Version==null || !Regex.IsMatch(payload.Version,@"^[0-9]+\.[0-9]+\.[0-9]+$") || !Version.TryParse(payload.Version,out _) || payload.Tag!="v"+payload.Version || payload.Asset!="Startwerk.exe" || payload.Sha256==null || !Regex.IsMatch(payload.Sha256,@"^[A-Fa-f0-9]{64}$") || payload.Size<1024 || payload.Size>536870912 || payload.Notes==null || payload.Notes.Length>8000)throw new IOException("Die signierten Update-Angaben passen nicht zu Startwerk.");
        return payload;
    }
    static bool AllowedHost(Uri uri) => uri.Scheme=="https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    public static HttpClient CreateClient()
    {
        var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Startwerk/"+VersionText);return client;
    }
    static async Task<HttpResponseMessage> Fetch(HttpClient client,Uri uri,CancellationToken token)
    {
        for(int redirect=0;redirect<6;redirect++)
        {
            if(!AllowedHost(uri))throw new IOException("Update-Download verweist auf ein nicht zugelassenes Ziel.");
            var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location=response.Headers.Location;response.Dispose();
                if(location==null)throw new IOException("Ungültige Download-Weiterleitung.");uri=new Uri(uri,location);continue;
            }
            return response;
        }
        throw new IOException("Zu viele Update-Weiterleitungen.");
    }
    static async Task<byte[]> Limited(HttpResponseMessage response,int maximum,CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>maximum)throw new IOException("Update-Antwort ist zu groß.");
        await using var stream=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buffer=new byte[8192];
        while(true){int n=await stream.ReadAsync(buffer,token);if(n==0)break;if(memory.Length+n>maximum)throw new IOException("Update-Antwort ist zu groß.");memory.Write(buffer,0,n);}return memory.ToArray();
    }
    public static async Task<AvailableUpdate?> Check(string repository,CancellationToken token=default,HttpClient? transport=null,string? publicKey=null,Version? currentVersion=null)
    {
        ValidateRepository(repository); using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var owned=transport==null?CreateClient():null;var client=transport??owned!;
        using var release=await Fetch(client,new Uri($"https://api.github.com/repos/{repository}/releases/latest"),timeout.Token);
        if(release.StatusCode==HttpStatusCode.NotFound)throw new IOException("Noch kein öffentliches Release für dieses GitHub-Projekt erreichbar.");
        using var json=JsonDocument.Parse(await Limited(release,262144,timeout.Token));var root=json.RootElement;
        if(root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())return null;
        string tag=root.GetProperty("tag_name").GetString()??"";
        if(!Regex.IsMatch(tag,@"^v[0-9]+\.[0-9]+\.[0-9]+$"))throw new IOException("Das Release verwendet keine unterstützte Versionsnummer.");
        string url=$"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/update.json";
        using var description=await Fetch(client,new Uri(url),timeout.Token);
        var envelope=JsonSerializer.Deserialize<SignedUpdate>(await Limited(description,98304,timeout.Token),Json)??throw new IOException("Update-Beschreibung fehlt.");
        var payload=Verify(envelope,repository,publicKey);
        if(payload.Tag!=tag)throw new IOException("Release und signierte Versionsnummer stimmen nicht überein.");
        if(Normalize(Version.Parse(payload.Version))<=Normalize(currentVersion??CurrentVersion))return null;
        return new(envelope,payload);
    }
    public static async Task<PreparedUpdate> Download(AvailableUpdate update,Action<string> log,CancellationToken token=default,HttpClient? transport=null,string? publicKey=null)
    {
        var payload=Verify(update.Envelope,update.Payload.Repository,publicKey);
        if(payload!=update.Payload)throw new IOException("Update-Angaben wurden verändert.");
        Directory.CreateDirectory(CacheDirectory);string folder=Path.Combine(CacheDirectory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"payload.exe");
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var owned=transport==null?CreateClient():null;var client=transport??owned!;
            string url=$"https://github.com/{payload.Repository}/releases/download/{Uri.EscapeDataString(payload.Tag)}/Startwerk.exe";
            using var response=await Fetch(client,new Uri(url),timeout.Token);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length && length!=payload.Size)throw new IOException("Download-Größe stimmt nicht mit dem signierten Update überein.");
            await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long total=0;int last=-1;var buffer=new byte[1024*1024];
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,buffer.Length,true))
            {
                while(true)
                {
                    int count=await input.ReadAsync(buffer,timeout.Token);if(count==0)break;total+=count;
                    if(total>payload.Size)throw new IOException("Update-Download überschreitet die erwartete Größe.");
                    hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),timeout.Token);
                    int percent=(int)(total*100/payload.Size);if(percent/10!=last){log($"Update laden: {percent} %");last=percent/10;}
                }
                await output.FlushAsync(timeout.Token);
            }
            if(total!=payload.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(payload.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Update-Prüfsumme stimmt nicht. Die Datei wird nicht installiert.");
            using(var check=File.OpenRead(path)){if(check.ReadByte()!=0x4d||check.ReadByte()!=0x5a)throw new IOException("Update ist keine Windows-Programmdatei.");}
            log("Update heruntergeladen; Signatur, Größe und SHA-256 geprüft.");return new(update,folder,path);
        }
        catch {if(File.Exists(path))File.Delete(path);throw;}
    }
    public static void BeginInstall(PreparedUpdate prepared)
    {
        var payload=Verify(prepared.Update.Envelope,prepared.Update.Payload.Repository);
        if(Normalize(Version.Parse(payload.Version))<=CurrentVersion)throw new IOException("Diese Update-Version ist nicht neuer als die installierte Version.");
        string self=Environment.ProcessPath??throw new IOException("Programmpfad fehlt.");
        if(Path.GetFileNameWithoutExtension(self).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new IOException("Automatische Installation ist nur in der veröffentlichten EXE verfügbar.");
        RejectRedirected(self);ValidateStage(prepared.Directory);
        string helper=Path.Combine(prepared.Directory,"updater.exe"),request=Path.Combine(prepared.Directory,"install.json");
        File.Copy(self,helper,false);
        using var process=Process.GetCurrentProcess();
        var command=new InstallRequest(prepared.Update.Envelope,prepared.Update.Payload.Repository,Path.GetFullPath(self),Hash(self),process.Id,process.StartTime.ToUniversalTime().Ticks);
        File.WriteAllText(request,JsonSerializer.Serialize(command,Json));
        var psi=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("--apply-update");psi.ArgumentList.Add(request);
        using var helperProcess=Process.Start(psi)??throw new IOException("Update-Hilfsprozess konnte nicht gestartet werden.");
    }
    public static void Apply(string requestPath,string? verificationKey=null)
    {
        string folder=Path.GetDirectoryName(Path.GetFullPath(requestPath))!;ValidateStage(folder);
        string self=Path.GetFullPath(Environment.ProcessPath!);
        if(self!=Path.Combine(folder,"updater.exe") || Path.GetFullPath(requestPath)!=Path.Combine(folder,"install.json"))throw new IOException("Ungültiger Update-Arbeitsordner.");
        var request=JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(requestPath),Json)??throw new IOException("Update-Auftrag fehlt.");
        var payload=Verify(request.Envelope,request.Repository,verificationKey);
        if(Normalize(Version.Parse(payload.Version))<=CurrentVersion)throw new IOException("Veraltetes Update wurde gesperrt.");
        string target=Path.GetFullPath(request.Target);RejectRedirected(target);
        if(!Hash(self).Equals(request.OldSha256,StringComparison.OrdinalIgnoreCase)||!Hash(target).Equals(request.OldSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Die bisherige Programmdatei hat sich geändert.");
        try
        {
            using var parent=Process.GetProcessById(request.ParentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=request.ParentStartTicks || !string.Equals(parent.MainModule?.FileName,target,StringComparison.OrdinalIgnoreCase))throw new IOException("Update-Auftrag gehört nicht zur laufenden Startwerk-Version.");
            if(!parent.WaitForExit(90000))throw new IOException("Startwerk wurde noch nicht beendet. Es wurde nichts ersetzt.");
        }
        catch(ArgumentException) { /* Parent already exited. The executable hash is checked again below. */ }
        string source=Path.Combine(folder,"payload.exe");RejectRedirected(source);
        string temporary=Path.Combine(Path.GetDirectoryName(target)!,".Startwerk-"+Guid.NewGuid().ToString("N")+".exe"),backup=Path.Combine(folder,"previous.exe");
        bool replaced=false;
        try
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length!=payload.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(payload.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Update-Datei wurde nach dem Download verändert.");
                input.Position=0;using var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
            }
            if(!Hash(target).Equals(request.OldSha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Programmdatei wurde während des Updates verändert.");
            File.Copy(target,backup,false);File.Replace(temporary,target,null);replaced=true;
            Process.Start(new ProcessStartInfo(target){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(target)!})?.Dispose();
            File.WriteAllText(Path.Combine(folder,"result.txt"),"Update auf "+payload.Version+" installiert.");
        }
        catch
        {
            if(replaced && File.Exists(backup))
            {
                if(File.Exists(temporary))File.Delete(temporary);File.Copy(backup,temporary,false);File.Replace(temporary,target,null);
            }
            throw;
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    static void ValidateStage(string folder)
    {
        folder=Path.GetFullPath(folder);string prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(CacheDirectory))+Path.DirectorySeparatorChar;
        if(!folder.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||Path.GetDirectoryName(folder)!=Path.TrimEndingDirectorySeparator(prefix)||!Guid.TryParseExact(Path.GetFileName(folder),"N",out _))throw new IOException("Ungültiger Update-Arbeitsordner.");RejectRedirected(folder);
    }
    static void RejectRedirected(string path)
    {
        string? item=Path.GetFullPath(path);while(item!=null){if((File.Exists(item)||Directory.Exists(item))&&(File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new IOException("Umgeleiteter Update-Pfad ist nicht zulässig.");item=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(item));}
    }
}
