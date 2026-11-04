using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Startwerk;

if(args.Length==3&&args[0]=="--rollback-fixture")
{
    string requestPath=args[1];var request=JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(requestPath))!;
    bool failed=false;try{Updates.Apply(requestPath,File.ReadAllText(args[2]));}catch(Exception){failed=true;}
    if(!failed||Updates.Hash(request.Target)!=request.OldSha256)Environment.Exit(1);
    Console.WriteLine("PASS: atomic EXE replacement rolled back after invalid executable could not start.");return;
}
int passed=0;void Pass(string label){passed++;Console.WriteLine("PASS: "+label);}
void Require(bool condition,string label){if(!condition)throw new Exception(label);Pass(label);}
async Task Reject(Func<Task> action,string label){bool rejected=false;try{await action();}catch(Exception ex)when(ex is IOException or CryptographicException or HttpRequestException){rejected=true;}Require(rejected,label);}
using var rsa=RSA.Create(2048);string key=rsa.ExportSubjectPublicKeyInfoPem();const string repo="sample/Startwerk";
byte[] exe=new byte[4096];RandomNumberGenerator.Fill(exe);exe[0]=0x4d;exe[1]=0x5a;
var payload=new UpdatePayload(repo,"2.0.0","v2.0.0","Startwerk.exe",Convert.ToHexString(SHA256.HashData(exe)),exe.Length,"Test release");
SignedUpdate Sign(UpdatePayload p){byte[] body=JsonSerializer.SerializeToUtf8Bytes(p);return new(Convert.ToBase64String(body),Convert.ToBase64String(rsa.SignData(body,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)));}
var envelope=Sign(payload);Require(Updates.Verify(envelope,repo,key)==payload,"valid signed manifest accepted");
await Reject(()=>Task.Run(()=>Updates.Verify(envelope with{Payload=Convert.ToBase64String(new byte[32])},repo,key)),"tampered manifest blocked");
await Reject(()=>Task.Run(()=>Updates.Verify(envelope,"other/Startwerk",key)),"repository substitution blocked");
using(var wrong=RSA.Create(2048))await Reject(()=>Task.Run(()=>Updates.Verify(envelope,repo,wrong.ExportSubjectPublicKeyInfoPem())),"unknown signing key blocked");
await Reject(()=>Task.Run(()=>Updates.Verify(Sign(payload with{Asset="evil.exe"}),repo,key)),"unexpected asset blocked");
await Reject(()=>Task.Run(()=>Updates.Verify(Sign(payload with{Asset="Brennwerk.exe"}),repo,key)),"Brennwerk executable cannot be installed as Startwerk");
Require(new UpdateSettings().Repository=="aheinen1971-cmd/Startwerk"&&Path.GetFileName(Updates.StateDirectory)=="Startwerk","Startwerk update channel and settings are separate from Brennwerk");
await Reject(()=>Task.Run(()=>Updates.Verify(Sign(payload with{Tag="v9.0.0"}),repo,key)),"tag/version mismatch blocked");
await Reject(()=>Task.Run(()=>Updates.ValidateRepository("sample/../../evil")),"repository path traversal blocked");
await Reject(()=>Task.Run(()=>Updates.Verify(new SignedUpdate(null!,null!),repo,key)),"missing signature fields blocked");
using var client=new HttpClient(new FakeGitHub(envelope,exe));
var available=await Updates.Check(repo,transport:client,publicKey:key,currentVersion:new Version(1,0,0));Require(available!=null,"GitHub latest release check finds newer signed version");
Require(await Updates.Check(repo,transport:client,publicKey:key,currentVersion:new Version(2,0,0))==null,"same version is not offered");
Require(await Updates.Check(repo,transport:client,publicKey:key,currentVersion:new Version(3,0,0))==null,"downgrade is not offered");
string? stage=null;
try
{
    var prepared=await Updates.Download(available!,_=>{},transport:client,publicKey:key);stage=prepared.Directory;
    Require(File.ReadAllBytes(prepared.PayloadPath).AsSpan().SequenceEqual(exe),"streamed download validates size, hash and Windows header");
    byte[] damaged=(byte[])exe.Clone();damaged[300]^=1;using var badClient=new HttpClient(new FakeGitHub(envelope,damaged));
    await Reject(async()=>{await Updates.Download(available!,_=>{},transport:badClient,publicKey:key);},"corrupt download rejected");
    using var shortClient=new HttpClient(new FakeGitHub(envelope,exe[..2048]));
    await Reject(async()=>{await Updates.Download(available!,_=>{},transport:shortClient,publicKey:key);},"truncated download rejected");
    using var redirect=new HttpClient(new FakeGitHub(envelope,exe,true));
    await Reject(async()=>{await Updates.Check(repo,transport:redirect,publicKey:key,currentVersion:new Version(1,0,0));},"HTTP and external redirect blocked");
    string self=Environment.ProcessPath!,helper=Path.Combine(stage,"updater.exe");
    foreach(string file in Directory.GetFiles(AppContext.BaseDirectory).Where(p=>Path.GetExtension(p) is ".exe" or ".dll" or ".json"))File.Copy(file,Path.Combine(stage,Path.GetFileName(file)),true);
    string target=Path.Combine(stage,"target.exe");File.Copy(helper,target);
    string requestFile=Path.Combine(stage,"install.json"),publicFile=Path.Combine(stage,"test-public.pem");
    File.WriteAllText(publicFile,key);File.WriteAllText(requestFile,JsonSerializer.Serialize(new InstallRequest(envelope,repo,target,Updates.Hash(helper),int.MaxValue,0)));
    var psi=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};psi.ArgumentList.Add("--rollback-fixture");psi.ArgumentList.Add(requestFile);psi.ArgumentList.Add(publicFile);
    using var child=Process.Start(psi)!;var stderr=child.StandardError.ReadToEndAsync();string output=await child.StandardOutput.ReadToEndAsync();await child.WaitForExitAsync();if(child.ExitCode!=0)throw new Exception(output+await stderr);Pass(output.Trim());
    Require(Updates.Hash(target)==Updates.Hash(helper),"old executable preserved after failed restart");
}
finally
{
    if(stage!=null)
    {
        string full=Path.GetFullPath(stage),root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Updates.CacheDirectory))+Path.DirectorySeparatorChar;
        if(full.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Guid.TryParseExact(Path.GetFileName(full),"N",out _))
        {
            for(int attempt=0;attempt<20;attempt++)
            {
                try{if(Directory.Exists(full))Directory.Delete(full,true);break;}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
                {
                    // Windows or antivirus may still hold the exited helper briefly.
                    // Protocol assertions above already completed; cleanup is best effort.
                    if(attempt==19)Console.WriteLine("NOTE: temporary test helper remains locked; cleanup deferred.");
                    else await Task.Delay(250);
                }
            }
        }
    }
}
Console.WriteLine($"{passed} update checks passed. No real application or media was replaced.");

sealed class FakeGitHub(SignedUpdate envelope,byte[] payload,bool redirect=false):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        HttpResponseMessage response;
        if(redirect){response=new(HttpStatusCode.Redirect);response.Headers.Location=new Uri("http://example.invalid/update");}
        else if(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest"))response=new(HttpStatusCode.OK){Content=new StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v2.0.0\"}")};
        else if(request.RequestUri.AbsolutePath.EndsWith("update.json"))response=new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(envelope))};
        else response=new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)};
        return Task.FromResult(response);
    }
}
