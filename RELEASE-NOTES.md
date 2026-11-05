Startwerk 0.2.1

Windows-ISOs mit einer install.esd über 4 GiB lassen sich jetzt auf FAT32-Sticks schreiben. Startwerk exportiert alle Editionen in eine temporäre WIM und teilt diese in install.swm, install2.swm und weitere Teile. Vorbereitung und Kapazitätsprüfung laufen vor dem Löschen des USB-Ziels. Die Original-ISO wird schreibgeschützt gelesen.

Leere Zieldatenträger ohne Partitionen werden jetzt auch bei der Prüfung der vorhandenen Volumes korrekt behandelt.
