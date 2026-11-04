using Startwerk;
int checks=0;
void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);checks++;}
void Reject(Action action,string text){try{action();}catch(IOException){Check(true,text);return;}throw new Exception("Unexpected acceptance: "+text);}
var root=Path.Combine(Path.GetTempPath(),"Startwerk-usb-plan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    string path=Path.Combine(root,"misleading-windows-name.iso");byte[] bytes=new byte[65536];bytes[510]=0x55;bytes[511]=0xAA;bytes[450]=0x83;bytes[458]=1;File.WriteAllBytes(path,bytes);
    var raw=await UsbImages.Inspect(path,_=>Task.FromResult(new IsoFacts(false,false,false)));
    Check(raw.Hybrid&&!raw.Windows&&raw.PartitionTable=="MBR","partitioned ISO recognized by content, not Windows filename");
    var rawPlan=UsbPlan.Create(raw,"Image");
    Check(rawPlan.Description.Contains("unverändert"),"raw image retains its partition scheme and filesystem");
    Reject(()=>UsbPlan.Create(raw,"UEFI"),"raw image cannot be falsely converted to UEFI");
    var windows=await UsbImages.Inspect(path,_=>Task.FromResult(new IsoFacts(true,true,true)));
    Check(windows.Windows,"Windows install files take precedence over hybrid signature");
    var win11=windows with{Windows11=true,Architecture="amd64"};
    Check(!UsbPlan.Create(win11,"UEFI").Windows11Compatibility,"Windows 11 compatibility stays off by default");
    foreach(var mode in new[]{"UEFI","BIOS","Both"})Check(UsbPlan.Create(win11,mode,true).Windows11Compatibility,"Windows 11 opt-in supported with "+mode);
    Check(UsbPlan.Create(win11 with{Architecture="arm64"},"UEFI",true).Windows11Compatibility,"ARM64 uses its own Windows 11 setup architecture");
    Reject(()=>UsbPlan.Create(windows,"UEFI",true),"unknown or older Windows cannot enable compatibility");
    Reject(()=>UsbPlan.Create(raw,"Image",true),"Linux raw image cannot enable Windows 11 compatibility");
    Reject(()=>UsbPlan.Create(win11 with{Architecture="x86"},"BIOS",true),"32-bit image cannot enable Windows 11 compatibility");
    var compatSummary=UsbPlan.Create(win11,"Both",true).Summary(new Target("usb","TEST",7,"TEST",16000000000,"SERIAL"),true);
    Check(compatSummary.Contains("Kompatibilität EIN")&&compatSummary.Contains("TPM")&&compatSummary.Contains("USB-Start")&&compatSummary.Contains("nicht garantiert"),"final confirmation explicitly lists compatibility and its limits");
    foreach(var mode in new[]{"UEFI","BIOS","Both"})Check(UsbPlan.Create(windows,mode).BootMode==mode,"Windows selection: "+mode);
    Check(UsbPlan.Create(windows,"UEFI").Description.Contains("GPT"),"UEFI uses GPT");
    Check(UsbPlan.Create(windows,"Both").Description.Contains("MBR"),"dual boot uses MBR");
    Reject(()=>UsbPlan.Create(windows with{Efi=false},"UEFI"),"missing EFI loader blocks UEFI before confirmation");
    Reject(()=>UsbPlan.Create(windows with{Bios=false},"BIOS"),"missing BIOS files block BIOS before confirmation");
    Reject(()=>UsbPlan.Create(windows with{Bios=false},"Both"),"dual mode requires both boot loaders");
    Reject(()=>UsbPlan.Create(windows,"invalid"),"unknown boot choice rejected");
    var unknown=windows with{Windows=false,Hybrid=false};Reject(()=>UsbPlan.Create(unknown,"Image"),"ordinary non-hybrid ISO rejected for USB raw writing");
    var summary=UsbPlan.Create(windows,"Both").Summary(new Target("usb","USB 77 · TEST · 16 GiB",77,"test",16L*1024*1024*1024,"SERIAL-77"),true);
    Check(summary.Contains("SERIAL-77")&&summary.Contains(path)&&summary.Contains("MBR")&&summary.Contains("gelöscht")&&summary.Contains("Ein"),"review includes exact destination, source, layout, erasure and verification");
    bytes[512]=(byte)'E';bytes[513]=(byte)'F';bytes[514]=(byte)'I';bytes[515]=(byte)' ';bytes[516]=(byte)'P';bytes[517]=(byte)'A';bytes[518]=(byte)'R';bytes[519]=(byte)'T';
    string img=Path.Combine(root,"disk.img");File.WriteAllBytes(img,bytes);
    Check((await UsbImages.Inspect(img,_=>throw new Exception("IMG must not mount"))).PartitionTable=="GPT","GPT IMG recognized without mounting");
    Array.Clear(bytes,512,8);System.Text.Encoding.ASCII.GetBytes("EFI PART").CopyTo(bytes,4096);File.WriteAllBytes(img,bytes);
    Check((await UsbImages.Inspect(img)).PartitionTable=="GPT","GPT image with 4 KiB sectors recognized");
    var lines=new List<string>();await Backend.TestEmbeddedUsb(lines.Add);
    Check(lines.Any(x=>x.Contains("16 Windows boot"))&&lines.Any(x=>x.Contains("9 rejection")),"real embedded PowerShell guard and boot matrix pass without device writes");
    Check(lines.Any(x=>x.Contains("Windows 11 answer file")),"real embedded answer generation, file SHA256, metadata classification and collision guards pass");
    if(args.Length==2&&args[0]=="--inspect")Console.WriteLine((await UsbImages.Inspect(args[1])).Description);
}
finally
{
    string full=Path.GetFullPath(root),prefix=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
    if(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("Startwerk-usb-plan-",StringComparison.Ordinal))Directory.Delete(full,true);
}
Console.WriteLine(checks+" USB planning checks passed. No USB device written.");
