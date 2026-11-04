# Startwerk-Updatekanal

Repository: https://github.com/aheinen1971-cmd/Startwerk

Signierte GitHub-Releases mit Startwerk.exe und update.json. Eigener Startwerk-Prüfschlüssel; Brennwerk-Releases sind keine gültigen Startwerk-Updates. Einstellungen und Cache liegen unter %LOCALAPPDATA%\Startwerk.

Neue Version im Projekt eintragen, RELEASE-NOTES.md aktualisieren und einen passenden vX.Y.Z-Tag veröffentlichen. Die Veröffentlichung prüft USB, Downloads, Webseiten und den Updater, baut die einzelne EXE und signiert das Manifest mit dem Repository-Secret STARTWERK_UPDATE_KEY. Der private Schlüssel wird nicht im Quellcode gespeichert.

Prüfung auf Programmupdates höchstens alle zwölf Stunden, bei relevanten Webseitenfehlern zusätzlich. Automatisches Installieren nur bei eingeschalteter Option und untätigem Programm. Die Webseitenprüfung alle sechs Stunden läuft nur bei geöffnetem Programm.
