# Startwerk

Startwerk ist die eigenständige USB-Version von Brennwerk. Der bisherige Brennwerk-Stand bleibt separat als Sicherung erhalten.

- Windows-Installations-USB: UEFI, BIOS oder BIOS + UEFI, soweit das gewählte Windows-Abbild dies unterstützt.
- Große `install.esd`-Dateien werden mit allen Editionen vor dem Löschen des Sticks exportiert und als FAT32-kompatible `install.swm`-Teile geschrieben. Dafür braucht Startwerk vorübergehend zusätzlichen freien Speicher auf dem Windows-PC.
- Linux-Hybrid-ISO und bootfähige IMG-Dateien unverändert auf USB schreiben. `IMG.XZ` kann direkt gewählt werden. Startwerk entpackt und prüft das Abbild auf dem PC, bevor ein Stick beschrieben wird.
- Windows-Downloadassistent mit offizieller Microsoft-Seite als Alternative.
- Linux-Katalog einschließlich Linux Mint und geprüfter alternativer Downloadseiten.
- Anbieter-Prüfsummen bei unterstützten direkten ISO-Downloads; Datenprüfung nach dem USB-Schreiben standardmäßig eingeschaltet.
- Datei-Prüfsumme berechnet SHA-256 für vorhandene Dateien und vergleicht auf Wunsch mit dem Wert des Anbieters.
- Stick prüfen beschreibt die gesamte gemeldete Kapazität mit Testdaten und vergleicht sie beim Zurücklesen. Die Restzeit wird je Phase geschätzt. Beim Abbruch wartet Startwerk auf den laufenden Zugriff und gibt den Stick frei. Stick wiederherstellen richtet ihn anschließend auf Wunsch als exFAT-Datenspeicher ein. Beide Vorgänge löschen Daten auf dem gewählten Stick.
- Sicherheitsprüfung des USB-Ziels und Eingabe der genauen Datenträgernummer vor dem Löschen.
- Webseitenprüfung alle sechs Stunden, solange Startwerk geöffnet ist; überfällige Prüfung beim nächsten Start.
- Eigener signierter GitHub-Updatekanal und eigene Einstellungen unter `%LOCALAPPDATA%\Startwerk`.

Die einzelne `Startwerk.exe` enthält das USB-Modul. Eine zusätzliche `usb.ps1` ist nicht nötig. Der Windows-Downloadassistent benötigt die Microsoft Edge WebView2 Runtime.

Startwerk enthält keine Disc-Brenn-, Disc-Lese-, Medienexport- oder Daten-ISO-Erstellungsfunktionen. Raspberry-Pi-Abbilder sind für Raspberry Pi bestimmt. Für `IMG.XZ` braucht Startwerk freien Platz für das vollständige entpackte Abbild (bis 64 GiB) und unterstützt einen XZ-Datenstrom mit CRC32, CRC64 oder SHA-256.

Die Alternativquellen für MX, Lubuntu und Raspberry Pi öffnen Downloadseiten im Browser. Die automatische aktuelle ISO-Auswahl samt Prüfsumme unterstützt Ubuntu, Debian, Fedora sowie Linux Mint Cinnamon, MATE und Xfce.

Die automatischen Tests schreiben keine echten USB-Sticks. Mit Version 0.3.0 wurde ein Windows-USB-Abbild in einer UEFI-VM mit aktivem Secure Boot bis zum Windows-Setup gestartet. Ob es auf einem bestimmten Rechner startet, hängt auch von den Schlüsseln und Sperrlisten seiner Firmware und den Startdateien des Abbilds ab.

## Bauen

```powershell
dotnet publish Startwerk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o package
```

## Windows 11 auf nicht unterstützter Hardware (ab 0.2.0)

Im Windows-USB-Bereich eine Windows-11-Client-ISO für x64 oder ARM64 auswählen. Nur bei lesbaren Installations-WIM-/ESD-Metadaten wird „Windows 11 für nicht unterstützte Hardware vorbereiten“ freigeschaltet. Die Option ist standardmäßig aus.

Beim USB-Start verwendet Windows Setup eine erzeugte autounattend.xml im Stammverzeichnis. Sie setzt ausschließlich BypassTPMCheck, BypassSecureBootCheck und BypassRAMCheck im LabConfig-Schlüssel des WindowsPE-Setups. Startwerk führt diese Registrierungsbefehle nicht auf dem Erstellungs-PC aus. Produktkey, Edition, Partitionierung der Windows-Zielplatte, Benutzerkonto und Aktivierung werden durch diese Antwortdatei nicht vorgegeben. Bestehende Antwortdateien werden nicht vermischt oder überschrieben; Startwerk stoppt in diesem Fall vor dem Löschen des USB-Ziels.

Keine Garantie für beliebige alte PCs: fehlende CPU-Befehle wie SSE4.2/POPCNT, 32-Bit-Prozessoren, fehlende Treiber und fehlende Ressourcen werden nicht ersetzt. Kein Umgehungsmodus für setup.exe innerhalb eines laufenden Windows. Nicht unterstützte Installationen erhalten keine Support-/Update-Garantie von Microsoft.

Quellen und Grenzen:
- https://learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-automation-overview?view=windows-11
- https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-setup-runsynchronous-runsynchronouscommand
- https://github.com/pbatard/rufus/wiki/FAQ
- https://support.microsoft.com/en-gb/windows/experience/compatibility/windows-11-on-devices-that-don-t-meet-minimum-system-requirements

Tests prüfen die erzeugte Datei und Schutzfälle; kein vollständiger Installationsversuch mit echter Windows-11-ISO auf alter Hardware durchgeführt.
