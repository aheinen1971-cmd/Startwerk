using System.IO;

namespace Startwerk;

public sealed record StickPass(
    DateTimeOffset WrittenAt, Target Target, UsbPlan Plan, string Sha256,
    string? Provider, bool? PublisherHashVerified, bool Readback, bool? EfiOnStick)
{
    public string? VirtualResult { get; set; }
    public string? SnapshotSha256 { get; set; }
    public string? PhysicalResult { get; set; }
    public bool CanProbe { get; set; } = true;
    public bool ManualHashMatched { get; init; }

    public static StickPass Record(Target target, UsbPlan plan, string digest, string? provider, string? downloadedDigest,
        bool? publisherHashVerified, bool readback, bool? efi)
    {
        bool sameFile = provider is not null && string.Equals(downloadedDigest, digest, StringComparison.OrdinalIgnoreCase);
        return new(DateTimeOffset.Now, target, plan, digest, sameFile ? provider : null,
            sameFile ? publisherHashVerified : null, readback, efi);
    }

    public bool SupportsSecureBootProbe =>
        CanProbe && Plan.Image.Windows && (Plan.BootMode is "UEFI" or "Both")
        && (Plan.Image.Architecture is "" or "amd64") && EfiOnStick == true;

    public string Report()
    {
        string source = Provider is null ? "Lokale Datei" : $"Download über Startwerk ({Provider})";
        string checksum = PublisherHashVerified == true ? "Anbieter-Prüfsumme stimmt überein"
            : ManualHashMatched ? "Manuell eingegebene Referenz stimmt überein"
            : "Keine Anbieter-Prüfsumme bestätigt";
        string efi = EfiOnStick switch
        {
            true => "x64-UEFI-Startdatei auf dem Stick gefunden.",
            false when (Plan.BootMode is "UEFI" or "Both") && (Plan.Image.Architecture is "" or "amd64") =>
                "x64-UEFI-Startdatei fehlt. Für einen x64-PC mit UEFI eine passende Windows-x64-ISO verwenden und UEFI auswählen.",
            _ => "x64-UEFI-Startdatei für diesen Schreibmodus nicht geprüft."
        };
        return $"""
            STARTWERK-STICKPASS

            Geschrieben am {WrittenAt:dd.MM.yyyy HH:mm zzz}
            Ziel  {Target.Label}
            Abbild  {Path.GetFileName(Plan.Image.Path)}
            Quelle  {source}
            SHA-256  {Sha256}
            Anbieterprüfung  {checksum}

            Schreibmodus  {Plan.BootMode}
            Rückleseprüfung  {(Readback ? "Bestanden" : "Abgewählt")}
            Startdatei  {efi}

            Virtueller Start  {VirtualResult ?? "Nicht geprüft"}
            SHA-256 der Kopie  {SnapshotSha256 ?? "Keine Kopie geprüft"}
            Start auf einem echten PC  {PhysicalResult ?? "Nicht dokumentiert"}
            """;
    }
}
