namespace Startwerk;
public sealed record LinuxDistribution(string Id,string Name,string Page,bool Direct,string Edition)
{
    public override string ToString()=>Name;
}
public sealed record LinuxAlternative(string Id,string Provider,string Name,string Page,string Evidence,string Note)
{
    public override string ToString()=>Name;
}
public static class LinuxCatalog
{
    public static IReadOnlyList<LinuxAlternative> Alternatives {get;}=Array.AsReadOnly<LinuxAlternative>([
        new("MX:mirror","MX","MX · offizieller Spiegelserver","https://rsync-mxlinux.org/MX/Final/","https://mxlinux.org/wiki/system/iso-download-mirrors/","Ausgabe auswählen und deren SHA-256-/Signaturdatei mit herunterladen."),
        new("MX:sourceforge","MX","MX · SourceForge-Projekt","https://sourceforge.net/projects/mx-linux/files/Final/","https://mxlinux.org/wiki/system/iso-download-mirrors/","Nur das MX-Linux-Projekt ist freigegeben; Ausgabe und Prüfsumme auf der Seite wählen."),
        new("Lubuntu:ubuntu","Lubuntu","Lubuntu · Ubuntu-CD-Server","https://cdimage.ubuntu.com/lubuntu/releases/","https://help.ubuntu.com/community/UbuntuHashes","Version und release-Ordner wählen; SHA256SUMS zur passenden ISO verwenden."),
        new("Raspberry:arm64","Raspberry","Raspberry Pi OS · 64-Bit-Archiv","https://downloads.raspberrypi.com/raspios_arm64/images/","https://www.raspberrypi.com/software/operating-systems/","Abbild für Raspberry Pi. IMG.XZ direkt in Startwerk auswählen; die Anbieter-Prüfsumme gilt für die heruntergeladene Datei."),
        new("Raspberry:armhf","Raspberry","Raspberry Pi OS · 32-Bit-Archiv","https://downloads.raspberrypi.com/raspios_armhf/images/","https://www.raspberrypi.com/software/operating-systems/","Abbild für Raspberry Pi. Passende Version wählen und IMG.XZ in Startwerk öffnen. Die Anbieter-Prüfsumme gilt für das Archiv.")
    ]);
    public static bool Matches(LinuxAlternative entry,Uri uri)
    {
        var official=new Uri(entry.Page);
        return WebsiteSafety.Https(uri)&&uri.IdnHost==official.IdnHost&&uri.AbsolutePath.StartsWith(official.AbsolutePath,StringComparison.Ordinal);
    }
    public static IReadOnlyList<LinuxDistribution> Items {get;}=Array.AsReadOnly<LinuxDistribution>([
        new("Ubuntu","Ubuntu Desktop LTS","https://ubuntu.com/download/desktop",true,"Desktop LTS · Intel/AMD 64 Bit"),
        new("Debian","Debian","https://www.debian.org/distrib/",true,"Netinstall · Intel/AMD 64 Bit"),
        new("Fedora","Fedora Workstation","https://fedoraproject.org/workstation/download",true,"Workstation · Intel/AMD 64 Bit"),
        new("Mint","Linux Mint Cinnamon","https://linuxmint.com/download.php",true,"Cinnamon · Intel/AMD 64 Bit"),
        new("MintMate","Linux Mint MATE","https://linuxmint.com/download.php",true,"MATE · Intel/AMD 64 Bit"),
        new("MintXfce","Linux Mint Xfce","https://linuxmint.com/download.php",true,"Xfce · Intel/AMD 64 Bit"),
        new("Arch","Arch Linux","https://archlinux.org/download/",false,"Rollende Veröffentlichung"),
        new("Manjaro","Manjaro","https://manjaro.org/products/download/x86",false,"Edition auf der offiziellen Seite wählen"),
        new("Endeavour","EndeavourOS","https://endeavouros.com/",false,"Offizielle Downloadseite"),
        new("OpenSUSE","openSUSE","https://get.opensuse.org/",false,"Leap oder Tumbleweed wählen"),
        new("Kali","Kali Linux","https://www.kali.org/get-kali/",false,"Installer oder Live-Ausgabe wählen"),
        new("Pop","Pop!_OS","https://system76.com/pop/",false,"Passende Grafikausgabe wählen"),
        new("Zorin","Zorin OS","https://zorin.com/os/download/",false,"Ausgabe auf der offiziellen Seite wählen"),
        new("Elementary","elementary OS","https://elementary.io/",false,"Offizielle Downloadseite"),
        new("MX","MX Linux","https://mxlinux.org/download-links/",false,"Desktop-Ausgabe wählen"),
        new("Tails","Tails","https://tails.net/install/",false,"Installationsanleitung und Abbild wählen"),
        new("Kubuntu","Kubuntu","https://kubuntu.org/getkubuntu/",false,"Ubuntu mit KDE Plasma"),
        new("Xubuntu","Xubuntu","https://xubuntu.org/download/",false,"Ubuntu mit Xfce"),
        new("Lubuntu","Lubuntu","https://lubuntu.me/downloads/",false,"Ubuntu mit LXQt"),
        new("UbuntuMate","Ubuntu MATE","https://ubuntu-mate.org/download/",false,"Ubuntu mit MATE"),
        new("Neon","KDE neon","https://neon.kde.org/download",false,"KDE-Plasma-Desktop"),
        new("Rocky","Rocky Linux","https://rockylinux.org/download",false,"Server-Ausgabe wählen"),
        new("Alma","AlmaLinux","https://almalinux.org/get-almalinux/",false,"Server-Ausgabe wählen"),
        new("Gentoo","Gentoo","https://www.gentoo.org/downloads/",false,"Abbild und Architektur wählen"),
        new("Nix","NixOS","https://nixos.org/download/",false,"Desktop oder minimale ISO wählen"),
        new("Alpine","Alpine Linux","https://alpinelinux.org/downloads/",false,"Standard oder Extended wählen"),
        new("Raspberry","Raspberry Pi OS","https://www.raspberrypi.com/software/operating-systems/",false,"Abbild für Raspberry Pi; andere Zielarchitektur")
    ]);
}
