using System.Net;
using System.Net.Http;
using System.Text.Json;
using Startwerk;
int checks=0;void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);checks++;}
string official=WebsiteSafety.Start("Arch");
Check(WebsiteSafety.Page("Arch",new Uri(official)),"pinned Arch page accepted");
Check(WebsiteSafety.Page("Arch",new Uri("https://www.archlinux.org/download/")),"explicit www alias accepted");
foreach(string bad in new[]{"https://archlinux.org.evil.invalid/download/","https://evilarchlinux.org/","https://evil.invalid/?next=https://archlinux.org/","https://archlinux.org@evil.invalid/","https://evil.invalid@archlinux.org/","http://archlinux.org/","https://archlinux.org:8443/","https://127.0.0.1/","https://archlinux.org./","https://аrchlinux.org/"})Check(!WebsiteSafety.Page("Arch",new Uri(bad)),"deceptive or unsafe page rejected: "+new Uri(bad).IdnHost);
Check(!WebsiteSafety.Https(new Uri("relative",UriKind.Relative)),"relative addresses rejected safely");
Check(WebsiteSafety.MicrosoftPage(new Uri(IsoDownloads.MicrosoftPage)),"official Microsoft ISO page accepted");
Check(WebsiteSafety.MicrosoftPage(new Uri(IsoDownloads.MicrosoftPage+"arm64")),"official Microsoft Arm64 page accepted");
Check(!WebsiteSafety.MicrosoftPage(new Uri("https://account.microsoft.com/")),"other Microsoft services not treated as download form");
Check(!WebsiteSafety.MicrosoftPage(new Uri("https://www.microsoft.com/redirect?url=https://evil.invalid")),"Microsoft redirector path not whitelisted");
Check(!WebsiteSafety.MicrosoftPage(new Uri(IsoDownloads.MicrosoftPage+"?next=https://evil.invalid")),"unexpected Microsoft query rejected");
Check(!WebsiteSafety.Page("Arch",new Uri("https://ubuntu.com/")),"legitimate but wrong provider rejected");
Check(LinuxCatalog.Alternatives.Count==5&&LinuxCatalog.Alternatives.All(x=>WebsiteSafety.Page(x.Id,new Uri(x.Page))),"five pinned publisher-linked alternatives accepted");
Check(!WebsiteSafety.Page("MX:sourceforge",new Uri("https://sourceforge.net/projects/other-project/files/Final/")),"SourceForge permission restricted to exact MX project folder");
Check(!WebsiteSafety.Page("MX:mirror",new Uri("https://rsync-mxlinux.org/ANTIX/")),"MX mirror permission does not include unrelated distributions");
Check(!WebsiteSafety.Page("Lubuntu:ubuntu",new Uri("https://cdimage.ubuntu.com/kubuntu/releases/")),"Lubuntu alternative restricted to its own release directory");
Check(!WebsiteSafety.Page("Raspberry:arm64",new Uri("https://downloads.raspberrypi.com/raspios_armhf/images/")),"Raspberry architecture-specific alternative does not silently change architecture");
Check(!WebsiteSafety.Page("MX:sourceforge",new Uri("https://sourceforge.net/projects/mx-linux/files/Final-evil/")),"similar mirror folder name cannot match trusted path prefix");
using(var fake=new HttpClient(new Fixture(_=>new(HttpStatusCode.OK))))Check((await WebsiteSafety.Check("Arch","Arch",official,transport:fake)).State=="Verified","successful HTTPS source receives verified result");
int requests=0;
using(var fake=new HttpClient(new Fixture(_=>{requests++;return new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://evil.invalid/download/")}};})))
{
    var result=await WebsiteSafety.Check("Arch","Arch",official,transport:fake);Check(result.State=="Blocked"&&!result.CanOpen&&requests==1,"foreign redirect blocked before contacting its destination");
}
requests=0;
using(var fake=new HttpClient(new Fixture(_=>{requests++;return new(HttpStatusCode.Redirect){Headers={Location=new Uri("http://archlinux.org/download/")}};})))Check((await WebsiteSafety.Check("Arch","Arch",official,transport:fake)).State=="Blocked"&&requests==1,"HTTPS downgrade blocked before request");
requests=0;
using(var fake=new HttpClient(new Fixture(_=>{requests++;return new(HttpStatusCode.OK);})))Check((await WebsiteSafety.Check("Arch","Arch","https://evil.invalid/",transport:fake)).State=="Blocked"&&requests==0,"modified catalogue URL rejected without any network access");
requests=0;
using(var fake=new HttpClient(new Fixture(_=>new(++requests==1?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK))))
{
    var result=await WebsiteSafety.Check("Arch","Arch",official,transport:fake);Check(result.CanOpen&&result.Repaired&&requests==2,"temporary provider failure automatically repaired by retry at original URL");
}
requests=0;
using(var fake=new HttpClient(new Fixture(_=>{requests++;throw new HttpRequestException("Certificate validation failed");})))
{
    var result=await WebsiteSafety.Check("Arch","Arch",official,transport:fake);Check(!result.CanOpen&&requests==2,"certificate/network failure never becomes trusted after retry");
}
using(var fake=new HttpClient(new Fixture(_=>new(HttpStatusCode.Forbidden))))
{
    var result=await WebsiteSafety.Check("Arch","Arch",official,transport:fake);Check(result.State=="Restricted"&&result.CanOpen&&result.FinalAddress==official,"HTTP 403 distinguished from bad TLS or foreign origin");
}
using(var fake=new HttpClient(new Fixture(_=>new(HttpStatusCode.NotFound))))Check(!(await WebsiteSafety.Check("Arch","Arch",official,transport:fake)).CanOpen,"missing page not approved for external opening");
using(var fake=new HttpClient(new Fixture(_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("/download/",UriKind.Relative)}})))Check((await WebsiteSafety.Check("Arch","Arch",official,transport:fake)).State=="Blocked","redirect loop remains blocked");
var now=DateTime.UtcNow;var report=new WebsiteReport(now,[]);
Check(!WebsiteSafety.Due(report,now.AddHours(6).AddTicks(-1)),"not due before six hours");
Check(WebsiteSafety.Due(report,now.AddHours(6)),"due exactly at six hours");
Check(WebsiteSafety.Due(null,now)&&WebsiteSafety.Due(report,now.AddHours(-1)),"missing report or clock rollback triggers fresh check");
string folder=Path.Combine(Path.GetTempPath(),"Startwerk-website-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string path=Path.Combine(folder,"websites.json");
try
{
    var rows=new[]{new WebsiteResult("Windows","Microsoft",WebsiteSafety.Start("Windows"),null,now,"Verified","fixture")}.Concat(LinuxCatalog.Items.Select(x=>new WebsiteResult(x.Id,x.Name,x.Page,null,now,"Verified","fixture"))).Concat(LinuxCatalog.Alternatives.Select(x=>new WebsiteResult(x.Id,x.Name,x.Page,null,now,"Verified","fixture"))).ToArray();
    report=new(now,rows);WebsiteSafety.Save(path,report);Check(WebsiteSafety.Load(path)?.Results.Length==WebsiteSafety.SourceCount,"complete known-source report saved and loaded including alternatives");
    File.WriteAllText(path,"broken json");Check(WebsiteSafety.Load(path)==null,"damaged report discarded for automatic rebuild");
    var changed=rows.ToArray();changed[1]=changed[1] with{FinalAddress="https://evil.invalid/"};WebsiteSafety.Save(path,report with{Results=changed});Check(WebsiteSafety.Load(path)==null,"cached malicious redirect cannot inherit verified state");
    changed=rows.ToArray();changed[1]=changed[1] with{Address="https://ubuntu.com/other-path"};WebsiteSafety.Save(path,report with{Results=changed});Check(WebsiteSafety.Load(path)==null,"modified saved start address rejected even on legitimate host");
    WebsiteSafety.Save(path,report with{CompletedUtc=now.AddDays(1)});Check(WebsiteSafety.Load(path)==null,"future saved time cannot suppress checking indefinitely");
    WebsiteSafety.Save(path,report);Check(!Directory.GetFiles(folder,"*.tmp").Any(),"atomic save leaves no partial report");
}
finally
{
    string full=Path.GetFullPath(folder),prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
    if(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("Startwerk-website-tests-",StringComparison.Ordinal))Directory.Delete(full,true);
}
Console.WriteLine(checks+" website checks passed. No live website, ISO or USB write used.");
sealed class Fixture(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken token)=>Task.FromResult(reply(message));
}
