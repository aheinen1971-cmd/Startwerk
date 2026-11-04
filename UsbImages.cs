using System.IO;
using System.Text;

namespace Startwerk;
public sealed record IsoFacts(bool WindowsInstall, bool Efi, bool Bios);
public sealed record UsbImage(string Path, long Bytes, bool Windows, bool Efi, bool Bios, bool Hybrid, string PartitionTable)
{
    public string Description => Windows
        ? "Windows-Installation · " + (Efi && Bios ? "UEFI und BIOS erkannt" : Efi ? "UEFI erkannt" : Bios ? "BIOS erkannt" : "kein unterstützter Bootloader erkannt")
        : Hybrid ? $"Partitioniertes Abbild ({PartitionTable}) · wird unverändert geschrieben; Bootfähigkeit hängt vom Abbild ab."
        : "Kein unterstütztes USB-Abbild erkannt.";
}
public sealed record UsbPlan(UsbImage Image, string BootMode, string Description)
{
    public static UsbPlan Create(UsbImage image, string bootMode)
    {
        if (!image.Windows)
        {
            if (!image.Hybrid) throw new IOException("Dieses Abbild ist weder eine Windows-Installations-ISO noch ein partitioniertes Hybrid-/IMG-Abbild.");
            if (bootMode != "Image") throw new IOException("Bei Linux-/IMG-Abbildern wird das Boot-Verfahren unverändert aus dem Abbild übernommen.");
            return new(image, bootMode, $"Abbild unverändert · {image.PartitionTable}\nPartitionen und Dateisysteme werden aus dem Abbild übernommen. Freier Restplatz wird nicht automatisch erweitert.");
        }
        bool efi = bootMode is "UEFI" or "Both", bios = bootMode is "BIOS" or "Both";
        if (!efi && !bios) throw new IOException("Bitte ein gültiges Boot-Verfahren auswählen.");
        if (efi && !image.Efi) throw new IOException("In der Windows-ISO wurde kein UEFI-Bootloader erkannt. Bitte BIOS wählen oder eine passende ISO verwenden.");
        if (bios && !image.Bios) throw new IOException("In der Windows-ISO fehlen die BIOS-Startdateien. Bitte UEFI wählen oder eine passende ISO verwenden.");
        return new(image, bootMode, (bootMode == "UEFI" ? "UEFI · GPT" : bootMode == "BIOS" ? "BIOS / Legacy · MBR" : "BIOS + UEFI · MBR") +
            " · FAT32\nWindows-Partition bis 31 GiB; übriger Platz bleibt unpartitioniert. Große install.wim wird geteilt. Die Auswahl ändert keine Windows-Hardwareanforderungen.");
    }
    public string Summary(Target target, bool verify) =>
        $"Ziel: {target.Label}\nDatenträgernummer: {target.Number}\nSeriennummer: {(string.IsNullOrWhiteSpace(target.Serial) ? "nicht gemeldet" : target.Serial)}\n\n" +
        $"Quelle: {Image.Path}\nGröße: {Image.Bytes / 1073741824.0:F2} GiB\nErkannt: {Image.Description}\n\n" +
        Description + $"\n\nDatenprüfung nach dem Schreiben: {(verify ? "Ein" : "Aus")}\n\nALLE Partitionen und Dateien auf dem Ziel werden gelöscht.";
}
public static class UsbImages
{
    public static async Task<UsbImage> Inspect(string path, Func<string, Task<IsoFacts>>? isoReader = null)
    {
        path = System.IO.Path.GetFullPath(path);
        long size;
        byte[] header = new byte[8192];
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            size = file.Length;
            if (size < 512) throw new IOException("Das Abbild ist zu klein oder leer.");
            file.ReadExactly(header.AsSpan(0, (int)Math.Min(size, header.Length)));
        }
        bool partition = false;
        for (int i = 0; i < 4; i++)
            partition |= header[450 + i * 16] != 0 && BitConverter.ToUInt32(header, 458 + i * 16) > 0;
        bool hybrid = header[510] == 0x55 && header[511] == 0xAA && partition;
        bool gpt = Encoding.ASCII.GetString(header, 512, 8) == "EFI PART" || Encoding.ASCII.GetString(header, 4096, 8) == "EFI PART";
        string table = hybrid ? gpt ? "GPT" : "MBR" : "keine Partitionstabelle";
        IsoFacts facts = new(false, false, false);
        if (System.IO.Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            try { facts = await (isoReader ?? Backend.InspectIso)(path); }
            catch (IOException) when (hybrid) { /* A raw hybrid image can be written without mounting its filesystem. */ }
        }
        return new(path, size, facts.WindowsInstall, facts.Efi, facts.Bios, hybrid, table);
    }
}
