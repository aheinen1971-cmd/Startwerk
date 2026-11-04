# Startwerk

Startwerk ist die eigenständige USB-Version von Brennwerk. Der bisherige Brennwerk-Stand bleibt separat als Sicherung erhalten.

- Windows-Installations-USB: UEFI, BIOS oder BIOS + UEFI, soweit das gewählte Windows-Abbild dies unterstützt.
- Linux-Hybrid-ISO und bootfähige IMG-Dateien unverändert auf USB schreiben.
- Windows-Downloadassistent mit offizieller Microsoft-Seite als Alternative.
- Linux-Katalog einschließlich Linux Mint und geprüfter alternativer Downloadseiten.
- Anbieter-Prüfsummen bei unterstützten direkten ISO-Downloads; Datenprüfung nach dem USB-Schreiben standardmäßig eingeschaltet.
- Sicherheitsprüfung des USB-Ziels und Eingabe der genauen Datenträgernummer vor dem Löschen.
- Webseitenprüfung alle sechs Stunden, solange Startwerk geöffnet ist; überfällige Prüfung beim nächsten Start.
- Eigener signierter GitHub-Updatekanal und eigene Einstellungen unter `%LOCALAPPDATA%\Startwerk`.

Die einzelne `Startwerk.exe` enthält das USB-Modul. Eine zusätzliche `usb.ps1` ist nicht nötig. Der Windows-Downloadassistent benötigt die Microsoft Edge WebView2 Runtime.

Startwerk enthält keine Disc-Brenn-, Disc-Lese-, Medienexport- oder Daten-ISO-Erstellungsfunktionen. Raspberry-Pi-Abbilder sind für Raspberry Pi bestimmt; IMG.XZ vor dem Schreiben entpacken.

Die Alternativquellen für MX, Lubuntu und Raspberry Pi öffnen Downloadseiten im Browser. Die automatische aktuelle ISO-Auswahl samt Prüfsumme unterstützt Ubuntu, Debian, Fedora sowie Linux Mint Cinnamon, MATE und Xfce.

Die automatischen Tests schreiben keine echten USB-Sticks. Ein realer Schreib- und Bootversuch auf Hardware bleibt gesondert zu prüfen.

## Bauen

```powershell
dotnet publish Startwerk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o package
```
