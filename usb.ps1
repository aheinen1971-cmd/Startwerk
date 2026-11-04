param([switch]$SelfTest, [string]$ApplicationPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)

function Assert-Target($Disk, $Request) {
    if ($null -eq $Disk -or $Disk.BusType -ne 'USB' -or $Disk.IsBoot -or $Disk.IsSystem -or $Disk.IsReadOnly -or $Disk.IsOffline) { throw 'Das Ziel ist kein freigegebener USB-Datentraeger.' }
    if ([string]::IsNullOrWhiteSpace($Request.UniqueId) -or $Disk.UniqueId -cne $Request.UniqueId -or [long]$Disk.Size -ne [long]$Request.Size -or ([string]$Disk.SerialNumber) -cne ([string]$Request.Serial) -or [int]$Disk.Number -ne [int]$Request.Number) { throw 'Das Ziellaufwerk hat sich geaendert. Bitte neu auswaehlen.' }
}
function Assert-LocalSource($Source, $DiskNumber) {
    $resolved = [StartwerkNative]::FinalPath($Source)
    if ($resolved -notmatch '^\\\\\?\\([A-Za-z]):\\') { throw 'Bitte ein Abbild auf einem lokalen Laufwerk verwenden.' }
    $letter = $Matches[1]
    $partitions = @(Get-Partition -DriveLetter $letter -ErrorAction Stop)
    if ($partitions.Count -ne 1 -or $partitions[0].DiskNumber -eq $DiskNumber) { throw 'Die Quelle darf nicht auf dem USB-Ziel liegen.' }
}
function Get-CheckedDisk($Request) {
    $disk = Get-Disk -Number $Request.Number -ErrorAction Stop
    Assert-Target $disk $Request
    return $disk
}
function Get-Volumes($DiskNumber) {
    return @(Get-Partition -DiskNumber $DiskNumber | ForEach-Object { $_.AccessPaths } | Where-Object { $_ -match '^\\\\\?\\Volume\{' } | Select-Object -Unique)
}
function Get-ImageFacts($Source) {
    $install = (Test-Path -LiteralPath (Join-Path $Source 'sources\install.wim')) -or (Test-Path -LiteralPath (Join-Path $Source 'sources\install.esd')) -or (Test-Path -LiteralPath (Join-Path $Source 'sources\install.swm'))
    $windows = (Test-Path -LiteralPath (Join-Path $Source 'sources\boot.wim')) -and $install
    $efi = @(Get-ChildItem -LiteralPath (Join-Path $Source 'efi\boot') -Filter 'boot*.efi' -File -ErrorAction SilentlyContinue).Count -gt 0
    $bios = (Test-Path -LiteralPath (Join-Path $Source 'bootmgr')) -and (Test-Path -LiteralPath (Join-Path $Source 'boot\bcd'))
    return [pscustomobject]@{WindowsInstall=[bool]$windows; Efi=[bool]$efi; Bios=[bool]$bios}
}
function Assert-BootMode($Mode, $Facts) {
    if ($Mode -notin @('UEFI','BIOS','Both')) { throw 'Ungueltiges Windows-Bootverfahren.' }
    if ($Mode -in @('UEFI','Both') -and -not $Facts.Efi) { throw 'UEFI-Bootdateien fehlen in der ISO.' }
    if ($Mode -in @('BIOS','Both') -and -not $Facts.Bios) { throw 'BIOS-Bootdateien fehlen in der ISO.' }
}

# Win32 operations are kept in one helper; locks are held through writing and verification.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class StartwerkNative {
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
 static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
 [DllImport("kernel32.dll", SetLastError=true)]
 static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, uint ilen, IntPtr output, uint olen, out uint returned, IntPtr overlapped);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
 static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder buffer, uint length, uint flags);
 public static string FinalPath(string path) {
  using(var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
   var b = new StringBuilder(32768); uint len=GetFinalPathNameByHandle(file.SafeFileHandle,b,(uint)b.Capacity,0);
   if(len==0 || len>=b.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error()); return b.ToString();
  }
 }
 static void Ioctl(SafeFileHandle h, uint code) { uint n; if(!DeviceIoControl(h,code,IntPtr.Zero,0,IntPtr.Zero,0,out n,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Laufwerk belegt oder nicht zugreifbar. Alle Dateien und Explorer-Fenster auf dem USB-Ziel schliessen."); }
 static List<SafeFileHandle> Lock(string[] volumes) {
  var handles=new List<SafeFileHandle>();
  try {
   foreach(string volume in volumes) {
    var h=CreateFile(volume.TrimEnd('\\'),0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
    if(h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    handles.Add(h); Ioctl(h,0x90018);
   }
   foreach(var h in handles) Ioctl(h,0x90020);
   return handles;
  } catch { foreach(var h in handles) h.Dispose(); throw; }
 }
 public static void CheckAvailable(string[] volumes) { var handles=Lock(volumes); foreach(var h in handles) h.Dispose(); }
 public static void ValidateImage(string path, long capacity, int sector) {
  if(sector!=512 && sector!=4096) throw new IOException("Nicht unterstuetzte Sektorgroesse.");
  using(var src=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
   if(src.Length<512 || src.Length>capacity || ((src.Length+sector-1)/sector)*sector>capacity) throw new IOException("Abbild leer, ungueltig oder groesser als das Ziel.");
   var head=new byte[512]; if(src.Read(head,0,head.Length)!=512) throw new IOException("Abbildkopf unvollstaendig.");
   bool partition=false; for(int i=0;i<4;i++) if(head[450+i*16]!=0 && BitConverter.ToUInt32(head,458+i*16)>0) partition=true;
   if(head[510]!=0x55 || head[511]!=0xAA || !partition) throw new IOException("Kein partitioniertes USB-Abbild erkannt. Bitte eine Hybrid-ISO oder ein vollstaendiges Datentraeger-IMG verwenden.");
   // Windows installation ISOs need file extraction rather than raw writing.
  }
 }
 public static void WriteRaw(string path, int number, long capacity, int sector, string[] volumes, bool verify) {
  ValidateImage(path,capacity,sector);
  using(var src=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
   var handles=Lock(volumes);
   try {
    using(var disk=new FileStream(@"\\.\PhysicalDrive"+number,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite,1048576,FileOptions.WriteThrough)) {
     var b=new byte[4*1024*1024]; long remaining=src.Length; long done=0; int last=-1;
     while(remaining>0) {
      int n=(int)Math.Min(b.Length,remaining); int got=0;
      while(got<n) {int r=src.Read(b,got,n-got); if(r==0) throw new EndOfStreamException(); got+=r;}
      int padded=((n+sector-1)/sector)*sector; Array.Clear(b,n,padded-n); disk.Write(b,0,padded); remaining-=n; done+=n;
      int percent=(int)(done*100/src.Length); if(percent/5!=last) { Console.WriteLine("Schreiben: "+percent+" %"); last=percent/5; }
     }
     disk.Flush(true);
     if(verify) {
      Console.WriteLine("USB-Abbild wird zurueckgelesen und verglichen ...");
      disk.Position=0; src.Position=0; remaining=src.Length; var check=new byte[b.Length];
      while(remaining>0) {
       int n=(int)Math.Min(b.Length,remaining); int aligned=((n+sector-1)/sector)*sector;
       int got=0; while(got<aligned) {int r=disk.Read(check,got,aligned-got); if(r==0) throw new EndOfStreamException(); got+=r;}
       got=0; while(got<n) {int r=src.Read(b,got,n-got); if(r==0) throw new EndOfStreamException(); got+=r;}
       for(int i=0;i<n;i++) if(b[i]!=check[i]) throw new IOException("Datenpruefung fehlgeschlagen: Abbild und USB unterscheiden sich.");
       remaining-=n;
      }
     }
    }
   } finally { foreach(var h in handles) h.Dispose(); }
  }
 }
}
'@

if ($SelfTest) {
    $bootChecks = 0
    foreach ($efi in @($false,$true)) {
        foreach ($bios in @($false,$true)) {
            foreach ($mode in @('UEFI','BIOS','Both','Invalid')) {
                $facts = [pscustomobject]@{Efi=$efi; Bios=$bios}
                $expected = ($mode -eq 'UEFI' -and $efi) -or ($mode -eq 'BIOS' -and $bios) -or ($mode -eq 'Both' -and $efi -and $bios)
                $allowed = $true
                try { Assert-BootMode $mode $facts } catch { $allowed=$false }
                if ($allowed -ne $expected) { throw "Boot-Schutztest fehlgeschlagen: $mode / $efi / $bios" }
                $bootChecks++
            }
        }
    }
    Write-Output "PASS: $bootChecks Windows boot capability/selection combinations validated before device writes."
    $request = [pscustomobject]@{Number=8; UniqueId='TEST-USB'; Size=16000000000; Serial='123'}
    $good = @{Number=8; UniqueId='TEST-USB'; Size=16000000000; SerialNumber='123'; BusType='USB'; IsBoot=$false; IsSystem=$false; IsReadOnly=$false; IsOffline=$false}
    Assert-Target ([pscustomobject]$good) $request
    $cases = @{BusType='SATA'; IsBoot=$true; IsSystem=$true; IsReadOnly=$true; IsOffline=$true; UniqueId='OTHER'; Size=1; SerialNumber='OTHER'; Number=7}
    foreach ($key in $cases.Keys) {
        $bad = $good.Clone(); $bad[$key] = $cases[$key]; $rejected = $false
        try { Assert-Target ([pscustomobject]$bad) $request } catch { $rejected = $true }
        if (-not $rejected) { throw "Schutztest fehlgeschlagen: $key" }
    }
    $testFile = Join-Path ([IO.Path]::GetTempPath()) ('Startwerk-test-' + [guid]::NewGuid() + '.img')
    try {
        $data = New-Object byte[] 4096; [IO.File]::WriteAllBytes($testFile,$data)
        $rejected=$false; try { [StartwerkNative]::ValidateImage($testFile,8192,512) } catch { $rejected=$true }
        if (-not $rejected) { throw 'Leeres Abbild akzeptiert' }
        $data[510]=0x55; $data[511]=0xAA; $data[450]=0x83; $data[454]=1; $data[458]=1; [IO.File]::WriteAllBytes($testFile,$data)
        [StartwerkNative]::ValidateImage($testFile,8192,512)
        $rejected=$false; try { [StartwerkNative]::ValidateImage($testFile,1024,512) } catch { $rejected=$true }
        if (-not $rejected) { throw 'Zu kleines Ziel akzeptiert' }
        $rejected=$false; try { [StartwerkNative]::ValidateImage($testFile,8192,123) } catch { $rejected=$true }
        if (-not $rejected) { throw 'Sektorgroesse nicht validiert' }
        $final=[StartwerkNative]::FinalPath($testFile)
        if ($final -notmatch '^\\\\\?\\[A-Za-z]:\\') { throw "Ungueltige Pfadaufloesung: $final" }
    } finally { Remove-Item -LiteralPath $testFile -ErrorAction SilentlyContinue }
    Write-Output 'PASS: USB guard (9 rejection cases), image validation (4 cases), final source path. No device writes.'
    exit 0
}

$mountedHere = $false; $imagePath = $null; $sourceLock = $null; $splitFolder = $null
try {
    $request = [Console]::ReadLine() | ConvertFrom-Json
    if ($null -eq $request) { throw 'Auftrag fehlt.' }
    if ($request.InspectOnly) {
        $imagePath = [IO.Path]::GetFullPath([string]$request.Path)
        if ([IO.Path]::GetExtension($imagePath) -ine '.iso') { throw 'Nur ISO-Abbilder koennen schreibgeschuetzt eingebunden werden.' }
        $sourceLock = [IO.File]::Open($imagePath, 'Open', 'Read', 'Read')
        $image = Get-DiskImage -ImagePath $imagePath -ErrorAction Stop
        if (-not $image.Attached) { $image = Mount-DiskImage -ImagePath $imagePath -Access ReadOnly -PassThru; $mountedHere=$true }
        $volume = @($image | Get-Volume)
        if ($volume.Count -ne 1 -or -not $volume[0].DriveLetter) { throw 'ISO-Dateisystem konnte nicht gelesen werden.' }
        Get-ImageFacts "$($volume[0].DriveLetter):\" | ConvertTo-Json -Compress
        return
    }
    $disk = Get-CheckedDisk $request
    $imagePath = [IO.Path]::GetFullPath([string]$request.Path)
    Assert-LocalSource $imagePath $disk.Number
    $applicationSource = if ($ApplicationPath) { $ApplicationPath } else { $PSCommandPath }
    if ([string]::IsNullOrWhiteSpace($applicationSource)) { throw 'Anwendungspfad fehlt.' }
    Assert-LocalSource $applicationSource $disk.Number
    # Keep source immutable throughout all preflight checks and writes.
    $sourceLock = [IO.File]::Open($imagePath, 'Open', 'Read', 'Read')
    if ($sourceLock.Length -eq 0) { throw 'Das Abbild ist leer.' }
    $volumes = [string[]]@(Get-Volumes $disk.Number)
    if (-not $request.Windows) {
        [StartwerkNative]::ValidateImage($imagePath,[long]$disk.Size,[int]$disk.LogicalSectorSize)
        $disk = Get-CheckedDisk $request
        Write-Output 'USB-Ziel geprueft. Volumes werden gesperrt.'
        [StartwerkNative]::WriteRaw($imagePath,$disk.Number,[long]$disk.Size,[int]$disk.LogicalSectorSize,$volumes,[bool]$request.Verify)
        Write-Output 'Fertig. Stick vor dem Booten sicher entfernen und neu verbinden.'
    } else {
        if ([IO.Path]::GetExtension($imagePath) -ine '.iso') { throw 'Windows-Modus benoetigt eine Installations-ISO.' }
        $image = Get-DiskImage -ImagePath $imagePath -ErrorAction Stop
        if (-not $image.Attached) { $image = Mount-DiskImage -ImagePath $imagePath -Access ReadOnly -PassThru; $mountedHere=$true }
        $volume = @($image | Get-Volume)
        if ($volume.Count -ne 1 -or -not $volume[0].DriveLetter) { throw 'ISO konnte keinem Laufwerk zugeordnet werden.' }
        $source = "$($volume[0].DriveLetter):\"
        $facts = Get-ImageFacts $source
        if (-not $facts.WindowsInstall) { throw 'Keine unterstuetzte Windows-Installations-ISO erkannt.' }
        $bootMode = [string]$request.BootMode
        Assert-BootMode $bootMode $facts
        $bios = $bootMode -in @('BIOS','Both')
        # Use the Windows-supplied tool, never an executable from the selected ISO.
        $bootsect = Join-Path $env:SystemRoot 'System32\bootsect.exe'
        if ($bios -and -not (Test-Path -LiteralPath $bootsect)) { throw 'Windows bootsect.exe fehlt; BIOS-Erstellung ist auf diesem PC nicht verfuegbar.' }
        $files = @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
        if ($files.Count -eq 0) { throw 'ISO enthaelt keine Dateien.' }
        $large = @($files | Where-Object { $_.Length -gt 4294967295 })
        $wim = Join-Path $source 'sources\install.wim'
        foreach ($file in $large) { if ($file.FullName -ine $wim) { throw "Datei zu gross fuer FAT32: $($file.Name). Nur install.wim kann automatisch geteilt werden." } }
        $partitionSize = [long]([Math]::Min(31GB, [long]$disk.Size - 16MB))
        $total = [long](($files | Measure-Object Length -Sum).Sum)
        if ($partitionSize -lt 1GB -or $total + 512MB -gt $partitionSize) { throw 'Zu wenig Platz. Die FAT32-Partition ist maximal 31 GiB gross; 512 MiB Reserve sind erforderlich.' }
        if ($large.Count -gt 0) {
            $splitFolder = Join-Path ([IO.Path]::GetTempPath()) ('Startwerk-split-' + [guid]::NewGuid())
            [IO.Directory]::CreateDirectory($splitFolder) | Out-Null
            $marker = Join-Path $splitFolder 'source-check.tmp'
            [IO.File]::WriteAllText($marker,'preflight'); Assert-LocalSource $marker $disk.Number
            Remove-Item -LiteralPath $marker
            $free = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($splitFolder).Substring(0,1))).Free
            if ($free -lt ($large[0].Length + 1GB)) { throw 'Zu wenig temporaerer Speicher zum Teilen von install.wim.' }
            Write-Output 'install.wim wird vor dem Loeschen lokal in FAT32-kompatible Teile zerlegt ...'
            & "$env:SystemRoot\System32\dism.exe" /English /Split-Image "/ImageFile:$wim" "/SWMFile:$(Join-Path $splitFolder 'install.swm')" /FileSize:3800 /CheckIntegrity
            if ($LASTEXITCODE -ne 0) { throw "DISM konnte install.wim nicht teilen (Code $LASTEXITCODE)." }
            $splitFiles = @(Get-ChildItem -LiteralPath $splitFolder -Filter '*.swm' -File)
            if ($splitFiles.Count -eq 0) { throw 'DISM hat keine SWM-Dateien erzeugt.' }
            $splitSize = [long](($splitFiles | Measure-Object Length -Sum).Sum)
            if ($total - $large[0].Length + $splitSize + 512MB -gt $partitionSize) { throw 'Die geteilte Windows-Installation passt nicht auf das Ziel.' }
        }
        [StartwerkNative]::CheckAvailable($volumes)
        $disk = Get-CheckedDisk $request
        Write-Output "USB $($disk.Number): Partitionstabelle wird neu erstellt ..."
        if ($disk.PartitionStyle -ne 'RAW') { $disk | Clear-Disk -RemoveData -RemoveOEM -Confirm:$false }
        $disk = Get-Disk -Number $request.Number
        $style = if ($bootMode -eq 'UEFI') { 'GPT' } else { 'MBR' }
        if ($disk.PartitionStyle -ne 'RAW') { throw 'USB-Datentraeger wurde nicht vollstaendig geleert.' }
        $disk | Initialize-Disk -PartitionStyle $style | Out-Null
        if ($style -eq 'GPT') {
            $partition = New-Partition -DiskNumber $request.Number -Size $partitionSize -AssignDriveLetter -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
        } else {
            $partition = New-Partition -DiskNumber $request.Number -Size $partitionSize -AssignDriveLetter -IsActive
        }
        $formatted = $partition | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'STARTWERK' -Force -Confirm:$false
        $destination = "$($formatted.DriveLetter):\"
        if (-not $formatted.DriveLetter) { throw 'USB-Ziel hat keinen Laufwerksbuchstaben erhalten.' }
        if ($bios) {
            $current = Get-CheckedDisk $request
            $assigned = @(Get-Partition -DriveLetter $formatted.DriveLetter -ErrorAction Stop)
            if ($assigned.Count -ne 1 -or $assigned[0].DiskNumber -ne $current.Number) { throw 'USB-Laufwerksbuchstabe hat sich geaendert.' }
            Write-Output 'BIOS-Bootcode wird auf das ausgewaehlte USB-Ziel geschrieben ...'
            & $bootsect /nt60 "$($formatted.DriveLetter):" /mbr
            if ($LASTEXITCODE -ne 0) { throw "BIOS-Bootcode konnte nicht geschrieben werden (Code $LASTEXITCODE)." }
        }
        Write-Output 'Windows-Dateien werden kopiert ...'
        $copied = 0
        foreach ($file in $files) {
            if ($file.Length -gt 4294967295) { continue }
            $relative = $file.FullName.Substring($source.Length)
            $target = Join-Path $destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy($file.FullName,$target,$true)
            $copied++
            if ($copied % 100 -eq 0) { Write-Output "$copied / $($files.Count) Dateien kopiert" }
        }
        if ($large.Count -gt 0) {
            foreach ($file in $splitFiles) { [IO.File]::Copy($file.FullName,(Join-Path $destination ('sources\' + $file.Name)),$true) }
        }
        if ($request.Verify) {
            Write-Output 'Dateien werden mit SHA-256 verglichen ...'
            $checked=0
            foreach ($file in $files) {
                if ($file.Length -gt 4294967295) { continue }
                $target = Join-Path $destination $file.FullName.Substring($source.Length)
                if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw "Datenpruefung fehlgeschlagen: $($file.Name)" }
                $checked++; if ($checked % 100 -eq 0) { Write-Output "$checked Dateien geprueft" }
            }
            if ($large.Count -gt 0) {
                foreach ($file in $splitFiles) {
                    $target = Join-Path $destination ('sources\' + $file.Name)
                    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw "Datenpruefung fehlgeschlagen: $($file.Name)" }
                }
            }
        }
        Write-Output "Windows-USB fertig: $bootMode / $style / FAT32. Restlicher Platz bleibt unpartitioniert."
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($mountedHere -and $imagePath) { Dismount-DiskImage -ImagePath $imagePath -ErrorAction SilentlyContinue | Out-Null }
    if ($null -ne $sourceLock) { $sourceLock.Dispose() }
    if ($splitFolder -and (Test-Path -LiteralPath $splitFolder)) {
        # This is the exact GUID directory created above, never an inferred disk path.
        $fullSplit = [IO.Path]::GetFullPath($splitFolder)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($fullSplit.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($fullSplit) -match '^Startwerk-split-[0-9a-f-]{36}$') { Remove-Item -LiteralPath $fullSplit -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
