using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Length==3&&args[0]=="keygen")
{
    if(File.Exists(args[1]))throw new IOException("Signing key already exists; refusing replacement.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    using var rsa=RSA.Create(3072);File.WriteAllText(args[1],rsa.ExportPkcs8PrivateKeyPem());File.WriteAllText(args[2],rsa.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine("Signing key created locally; public key exported.");return;
}
if(args.Length==3&&args[0]=="verify")
{
    using var document=JsonDocument.Parse(File.ReadAllText(args[1]));var root=document.RootElement;
    using var checker=RSA.Create();checker.ImportFromPem(File.ReadAllText(args[2]));
    if(!checker.VerifyData(Convert.FromBase64String(root.GetProperty("Payload").GetString()!),Convert.FromBase64String(root.GetProperty("Signature").GetString()!),HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new IOException("Release does not match the pinned public key.");
    Console.WriteLine("Release signature matches the pinned public key.");return;
}
if(args.Length!=7||args[0]!="sign")throw new ArgumentException("Usage: sign EXE REPOSITORY VERSION PRIVATE_PEM NOTES_FILE OUTPUT_JSON; or verify JSON PUBLIC_PEM");
string exe=args[1],repository=args[2],version=args[3];
if(!Regex.IsMatch(repository,@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]+$")||!Regex.IsMatch(version,@"^[0-9]+\.[0-9]+\.[0-9]+$"))throw new ArgumentException("Invalid repository or version.");
string product=FileVersionInfo.GetVersionInfo(exe).ProductVersion??"";
if(product.Split('+')[0]!=version)throw new IOException("Executable version does not match release version.");
string notes=File.ReadAllText(args[5]);if(notes.Length>8000)notes=notes[..8000];
using var input=File.OpenRead(exe);
var payload=new{Repository=repository,Version=version,Tag="v"+version,Asset="Startwerk.exe",Sha256=Convert.ToHexString(SHA256.HashData(input)),Size=input.Length,Notes=notes};
byte[] body=JsonSerializer.SerializeToUtf8Bytes(payload);
using var signer=RSA.Create();signer.ImportFromPem(File.ReadAllText(args[4]));
string signature=Convert.ToBase64String(signer.SignData(body,HashAlgorithmName.SHA256,RSASignaturePadding.Pss));
File.WriteAllText(args[6],JsonSerializer.Serialize(new{Payload=Convert.ToBase64String(body),Signature=signature},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Signed release "+version+" for "+repository+".");
