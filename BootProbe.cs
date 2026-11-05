using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Startwerk;

static class BootProbe
{
    public const string CodeFile = "OVMF_CODE_4M.secboot.fd";
    public const string KeysFile = "OVMF_VARS_4M.ms.fd";
    const string CodeHash = "1286F2A3217D09B374D05951355D7A561C8BACECD74845C8A1FBB71A48C0DE8D";
    const string KeysHash = "A9B2B13ABA3D4629593913471AF7545BD8EEF1D414F33CAB4C86FFF7D5BC09B9";

    public static async Task<string> Run(Target target, string qemu, string firmware, Action<string> log, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "Startwerk-Bootprobe-" + Guid.NewGuid().ToString("N"));
        var drive = new DriveInfo(Path.GetPathRoot(temporary)!);
        if (drive.DriveType != DriveType.Fixed || target.Size <= 0 || drive.AvailableFreeSpace - 512L * 1024 * 1024 < target.Size)
            throw new IOException("Die Bootprobe braucht auf dem internen temporären Laufwerk freien Platz in Größe des Sticks plus 512 MiB.");
        string code = Path.Combine(temporary, "code.fd"), keys = Path.Combine(temporary, "keys.fd");
        string image = Path.Combine(temporary, "stick.img");
        Directory.CreateDirectory(temporary);
        try
        {
            CopyFirmware(Path.Combine(firmware, CodeFile), code, 3653632, CodeHash);
            CopyFirmware(Path.Combine(firmware, KeysFile), keys, 540672, KeysHash);
            using (File.Open(image, FileMode.CreateNew)) { }
            log("Der ausgewählte Stick wird schreibgeschützt auf den PC kopiert.");
            string digest = await Backend.CopyUsbForProbe(target, image, log, token);
            if (new FileInfo(image).Length != target.Size) throw new IOException("Die Kopie des USB-Sticks ist unvollständig.");
            log("Die virtuelle UEFI-Bootprobe wird gestartet.");
            await RunQemu(BuildStartInfo(qemu, image, code, keys), token);
            return digest;
        }
        finally
        {
            File.Delete(image);
            File.Delete(keys);
            File.Delete(code);
            Directory.Delete(temporary);
        }
    }

    static void CopyFirmware(string source, string destination, int length, string expected)
    {
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != length) throw new IOException("Die gewählte OVMF-Firmware passt nicht zum geprüften Startwerk-Profil.");
        byte[] data = new byte[length];
        input.ReadExactly(data);
        if (Convert.ToHexString(SHA256.HashData(data)) != expected)
            throw new IOException("Die Prüfsumme der OVMF-Firmware passt nicht zum geprüften Startwerk-Profil.");
        using var output = File.Open(destination, FileMode.CreateNew);
        output.Write(data);
    }

    internal static ProcessStartInfo BuildStartInfo(string qemu, string image, string code, string keys)
    {
        static string Value(string path) => Path.GetFullPath(path).Replace(",", ",,");
        var start = new ProcessStartInfo(Path.GetFullPath(qemu))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(qemu))!
        };
        foreach (string arg in new[]
        {
            "-name", "Startwerk · UEFI-Bootprobe", "-no-user-config", "-machine", "q35,smm=on",
            "-accel", "tcg", "-cpu", "max", "-smp", "2", "-m", "4096", "-nic", "none",
            "-display", "gtk", "-monitor", "none", "-serial", "none",
            "-global", "driver=cfi.pflash01,property=secure,value=on",
            "-drive", $"if=pflash,format=raw,readonly=on,file={Value(code)}",
            "-drive", $"if=pflash,format=raw,file={Value(keys)}",
            "-device", "qemu-xhci,id=usb3",
            "-drive", $"if=none,format=raw,readonly=on,id=stick,file={Value(image)}",
            "-device", "usb-storage,bus=usb3.0,drive=stick,bootindex=1,removable=on"
        }) start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task RunQemu(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("QEMU konnte nicht gestartet werden.");
        var errors = process.StandardError.ReadToEndAsync();
        using var stop = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        await process.WaitForExitAsync();
        string stderr = await errors;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("QEMU konnte die Bootprobe nicht ausführen.\n" + stderr.Trim());
    }
}
