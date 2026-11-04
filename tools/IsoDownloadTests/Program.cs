using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Startwerk;

int checks=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;Console.WriteLine("PASS: "+name);}
async Task Reject(Func<Task> action,string name){bool blocked=false;try{await action();}catch(Exception ex)when(ex is IOException or HttpRequestException or OperationCanceledException){blocked=true;}Check(blocked,name);}
Check(IsoDownloads.TrustedUri("Windows",new Uri("https://software.download.prss.microsoft.com/dbazure/Win.iso?token=example")),"official Microsoft download accepted");
Check(!IsoDownloads.TrustedUri("Windows",new Uri("https://software.download.prss.microsoft.com.evil.invalid/Win.iso")),"Microsoft lookalike domain rejected");
Check(!IsoDownloads.TrustedUri("Ubuntu",new Uri("http://releases.ubuntu.com/test.iso")),"unencrypted download rejected");
Check(!IsoDownloads.TrustedUri("Debian",new Uri("https://user:password@cdimage.debian.org/test.iso")),"URL credentials rejected");
Check(!IsoDownloads.TrustedUri("Fedora",new Uri("https://dl.fedoraproject.org:8443/test.iso")),"unexpected port rejected");
Check(IsoDownloads.TrustedUri("Debian",new Uri("https://saimei.ftp.acc.umu.se/debian-cd/current/amd64/iso-cd/debian.iso")),"official Debian storage redirect accepted");
Check(!IsoDownloads.TrustedUri("Debian",new Uri("https://saimei.ftp.acc.umu.se.evil.invalid/debian.iso")),"Debian storage lookalike domain rejected");
await Reject(()=>Task.Run(()=>IsoDownloads.MicrosoftSource("https://download.microsoft.com/MediaCreationTool.exe",null)),"Microsoft executables are not run or treated as ISOs");
await Reject(()=>Task.Run(()=>IsoDownloads.MicrosoftSource("https://download.microsoft.com/Windows.iso","bad")),"invalid publisher checksum rejected before download");
Check(IsoDownloads.UbuntuVersion("<a href='24.04/'>LTS</a><a href='26.04/'>LTS</a><a href='26.10/'>new</a>")=="26.04","newest stable Ubuntu LTS folder selected");
Check(IsoDownloads.IsoName("<a href='ubuntu-26.04-desktop-amd64.iso'></a><a href='ubuntu-26.04.1-desktop-amd64.iso'></a><a href='ubuntu-26.04.1-live-server-amd64.iso'></a>","Ubuntu")=="ubuntu-26.04.1-desktop-amd64.iso","latest Ubuntu desktop point release selected");
Check(IsoDownloads.IsoName("<a href='debian-13.7.0-amd64-netinst.iso'></a><a href='debian-edu-13.7.0-amd64-netinst.iso'></a>","Debian")=="debian-13.7.0-amd64-netinst.iso","standard Debian netinstall selected instead of alternate editions");
byte[] iso=new byte[65536];Encoding.ASCII.GetBytes("CD001").CopyTo(iso,32769);string hash=Convert.ToHexString(SHA256.HashData(iso));
Check(LinuxCatalog.Items.Count==27&&LinuxCatalog.Items.Select(x=>x.Id).Distinct().Count()==27&&LinuxCatalog.Items.All(x=>new Uri(x.Page).Scheme=="https"),"broad Linux catalogue has unique entries and HTTPS official pages");
Check(IsoDownloads.MintEdition("Cinnamon Edition <a href='edition.php?id=326'>Download</a>","Cinnamon")=="https://linuxmint.com/edition.php?id=326","Mint edition discovered from publisher page without fixed release ID");
var mint=IsoDownloads.MintSource("https://pub.linuxmint.io/stable/22.3/linuxmint-22.3-cinnamon-64bit.iso","Mint","Cinnamon");
Check(mint.Version=="22.3"&&mint.Url.Host=="pub.linuxmint.io","Mint source comes from publisher-linked stable server");
await Reject(()=>Task.Run(()=>IsoDownloads.MintSource("https://pub.linuxmint.io/stable/22.3-beta/linuxmint-22.3-cinnamon-64bit.iso","Mint","Cinnamon")),"Mint beta URL not treated as stable release");
Check(!IsoDownloads.TrustedUri("Mint",new Uri("https://pub.linuxmint.io.evil.invalid/test.iso")),"Mint lookalike domain rejected");
string windowsLink="https://software.download.prss.microsoft.com/dbazure/windows.iso?fresh=example";
var windowsData=new MicrosoftPageData([windowsLink],["Deutsch 64-bit "+hash]);
Check(MicrosoftWizard.Source(windowsData,"Deutsch").Sha256==hash,"wizard joins fresh Microsoft link and selected-language checksum");
Check(MicrosoftWizard.Source(windowsData with{Rows=["Englisch 64-bit "+hash,"Englisch (international) 64-bit "+new string('B',64)]},"Englisch (Vereinigte Staaten)").Sha256==hash,"US English form label matches US checksum without confusing international English");
await Reject(()=>Task.Run(()=>MicrosoftWizard.Source(windowsData,"Englisch")),"checksum for another Windows language rejected");
await Reject(()=>Task.Run(()=>MicrosoftWizard.Source(windowsData with{Links=[windowsLink,"https://download.microsoft.com/other.iso"]},"Deutsch")),"ambiguous Microsoft ISO links rejected");
await Reject(()=>Task.Run(()=>MicrosoftWizard.Source(windowsData with{Links=["https://download.microsoft.com.evil.invalid/windows.iso"]},"Deutsch")),"wizard rejects unofficial Windows source");
var wrongPage=new MicrosoftWizard(_=>throw new Exception("Must not execute"),()=>"https://example.com/");
await Reject(()=>wrongPage.Options("product-edition",CancellationToken.None),"wizard never executes form code outside Microsoft origin");
string previousLink="https://software.download.prss.microsoft.com/dbazure/previous-language.iso";int reads=0;
var changingForm=new MicrosoftWizard(_=>Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new MicrosoftPageData(++reads==1?[previousLink]:[previousLink,windowsLink],["Deutsch 64-bit "+hash]))),()=>IsoDownloads.MicrosoftPage);
Check((await changingForm.Link("Deutsch",CancellationToken.None,new HashSet<string>{previousLink})).Url.AbsoluteUri==windowsLink&&reads==2,"stale previous-language link is excluded until fresh form result arrives");
var blockedForm=new MicrosoftWizard(_=>Task.FromResult(System.Text.Json.JsonSerializer.Serialize(windowsData with{Error="Request unavailable"})),()=>IsoDownloads.MicrosoftPage);
await Reject(()=>blockedForm.Link("Deutsch",CancellationToken.None),"visible Microsoft refusal stops wizard without bypassing the provider");
Check(IsoDownloads.Checksum(hash.ToLowerInvariant()+" *sample.iso\n","sample.iso")==hash,"official SHA256SUMS format parsed");
await Reject(()=>Task.Run(()=>IsoDownloads.Checksum(hash+"  other.iso","sample.iso")),"checksum for wrong ISO rejected");
string json="[{\"version\":\"45 Beta\",\"arch\":\"x86_64\",\"variant\":\"Workstation\"},{\"version\":\"44\",\"arch\":\"x86_64\",\"variant\":\"Workstation\",\"link\":\"https://download.fedoraproject.org/pub/Fedora.iso\",\"sha256\":\""+hash+"\",\"size\":\"65536\"}]";
var fedora=IsoDownloads.FedoraSource(json);Check(fedora.Version=="44"&&fedora.Url.Host=="dl.fedoraproject.org","Fedora beta skipped and canonical Fedora server selected");
string folder=Path.Combine(Path.GetTempPath(),"Startwerk-iso-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
    string destination=Path.Combine(folder,"test.iso");var source=new IsoSource("Ubuntu","Test","26.04.1",new Uri("https://releases.ubuntu.com/26.04/test.iso"),"test.iso",hash,iso.Length);
    using var good=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(iso)}));
    var downloaded=await IsoDownloads.Download(source,destination,_=>{},_=>{},transport:good);
    Check(downloaded.PublisherHashVerified&&File.ReadAllBytes(destination).AsSpan().SequenceEqual(iso),"ISO download, SHA-256 and filesystem validation succeed");
    var debianSource=source with{Provider="Debian",Url=new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/test.iso")};
    using var debianRedirect=new HttpClient(new Fixture(request=>{if(request.RequestUri!.Host=="cdimage.debian.org"){var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("https://saimei.ftp.acc.umu.se/debian-cd/current/amd64/iso-cd/test.iso");return r;}return new(HttpStatusCode.OK){Content=new ByteArrayContent(iso)};}));
    Check((await IsoDownloads.Download(debianSource,Path.Combine(folder,"debian.iso"),_=>{},_=>{},transport:debianRedirect)).PublisherHashVerified,"Debian redirect keeps official checksum verification");
    byte[] previous=File.ReadAllBytes(destination),bad=(byte[])iso.Clone();bad[300]^=1;
    using var corrupt=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bad)}));
    await Reject(()=>IsoDownloads.Download(source,destination,_=>{},_=>{},transport:corrupt),"changed ISO rejected by publisher checksum");
    Check(previous.AsSpan().SequenceEqual(File.ReadAllBytes(destination)),"failed download preserves existing destination");
    using var shortened=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(iso[..32768])}));
    await Reject(()=>IsoDownloads.Download(source,destination,_=>{},_=>{},transport:shortened),"truncated ISO rejected");
    using var html=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK){Content=new StringContent("<html>expired link</html>",Encoding.UTF8,"text/html")}));
    await Reject(()=>IsoDownloads.Download(source,destination,_=>{},_=>{},transport:html),"HTML error page rejected instead of saving ISO");
    using var redirect=new HttpClient(new Fixture(_=>{var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("https://untrusted.invalid/test.iso");return r;}));
    await Reject(()=>IsoDownloads.Download(source,destination,_=>{},_=>{},transport:redirect),"redirect outside official provider rejected");
    byte[] invalid=new byte[65536];var invalidSource=source with{Sha256=Convert.ToHexString(SHA256.HashData(invalid))};
    using var noIso=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(invalid)}));
    await Reject(()=>IsoDownloads.Download(invalidSource,destination,_=>{},_=>{},transport:noIso),"file without ISO/UDF descriptors rejected despite matching hash");
    using var cancel=new CancellationTokenSource();cancel.Cancel();
    await Reject(()=>IsoDownloads.Download(source,destination,_=>{},_=>{},cancel.Token,good),"canceled download exits without replacing destination");
    Check(Directory.GetFiles(folder,"*.partial").Length==0,"partial downloads removed");
    using var catalog=new HttpClient(new Fixture(request=>new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath switch{"/"=>"<a href='26.04/'>Ubuntu LTS</a>","/26.04/"=>"<a href='ubuntu-26.04.1-desktop-amd64.iso'>ISO</a>",_=>hash+" *ubuntu-26.04.1-desktop-amd64.iso"})}));
    Check((await IsoDownloads.LatestLinux("Ubuntu",transport:catalog)).Version=="26.04.1","release discovery joins official directory and checksum");
    using var mintCatalog=new HttpClient(new Fixture(request=>new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath switch{"/download.php"=>"Cinnamon Edition <a href='edition.php?id=326'>Download</a>","/edition.php"=>"https://pub.linuxmint.io/stable/22.3/linuxmint-22.3-cinnamon-64bit.iso",_=>hash+" *linuxmint-22.3-cinnamon-64bit.iso"})}));
    Check((await IsoDownloads.LatestLinux("Mint",transport:mintCatalog)).Sha256==hash,"Mint discovery fetches matching checksum automatically");
    if(args.Contains("--live"))
    {
        foreach(string provider in new[]{"Ubuntu","Debian","Fedora","Mint","MintMate","MintXfce"})
        {
            var live=await IsoDownloads.LatestLinux(provider);Check(live.Sha256?.Length==64&&live.FileName.EndsWith(".iso"),"live official "+provider+" catalogue: "+live.Version);Console.WriteLine("Source: "+live.Url.GetLeftPart(UriPartial.Path));
        }
    }
}
finally
{
    string full=Path.GetFullPath(folder),prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
    if(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("Startwerk-iso-tests-",StringComparison.Ordinal))Directory.Delete(full,true);
}
Console.WriteLine(checks+" ISO download checks passed. No real ISO or USB device was downloaded/written.");
sealed class Fixture(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));
}
