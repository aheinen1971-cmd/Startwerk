Startwerk 0.2.0

Windows 11: optionale Vorbereitung für nicht unterstützte Hardware.
Nach Erkennung einer Windows-11-Client-ISO (x64 oder ARM64) kann die Option eingeschaltet werden. Beim Start des Windows-Setups vom USB-Stick werden TPM-, Secure-Boot- und RAM-Prüfungen mit einer WindowsPE-Antwortdatei ausgenommen.
Die Option ist standardmäßig aus, gilt nicht für ein laufendes Windows-Upgrade und erscheint in der letzten USB-Zusammenfassung.
Vorhandene Antwortdateien führen vor dem Löschen des USB-Ziels zum Abbruch. Die erzeugte Datei wird immer mit SHA-256 nachgelesen.
Fehlende CPU-Befehle (beispielsweise SSE4.2/POPCNT), Treiber und Speicher werden nicht ersetzt. Microsoft garantiert für nicht unterstützte Hardware weder Support noch Updates.
Windows-Systemmodule werden unabhängig von einer parallel installierten PowerShell 7 geladen.
Automatisierte Tests prüfen Metadaten, XML, Dateiprüfsumme, Kollisionsschutz und Oberfläche ohne reale USB-Schreibvorgänge. Ein tatsächlicher Installations-/Bootversuch auf alter Hardware steht aus.
