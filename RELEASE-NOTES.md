Startwerk 0.3.0

- Stick prüfen schreibt und prüft Testdaten über die gesamte gemeldete Kapazität.
- Stick wiederherstellen formatiert den ausgewählten Stick als exFAT-Datenspeicher.
- Die Datei-Prüfsumme berechnet SHA-256 und vergleicht sie optional mit dem Wert des Anbieters.
- Bootfähige IMG.XZ-Dateien lassen sich direkt auswählen. Startwerk prüft die XZ-Blockdaten und entpackt sie vor dem Schreiben.
- Der Start eines Windows-Installationsmediums mit großer install.esd wurde in einer UEFI-VM mit aktivem Secure Boot bis zum Windows-Setup geprüft.

Stickprüfung und Wiederherstellung löschen alle Daten auf dem ausgewählten Stick. Startwerk verlangt dafür die Eingabe der Datenträgernummer.
