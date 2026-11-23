# MilfTV - Versionshinweise

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Alle wichtigen Änderungen an MilfTV, die neueste zuerst.

## 1.0.0-beta.1

*Veröffentlicht am 2026-11-23*

Erste öffentliche Beta von MilfTV, einem IPTV-Player für Windows. Diese Version vereint alles, was bisher entstanden ist; hier ein vollständiger Überblick über ihre Funktionen. Wie jede Beta kann sie noch Fehler enthalten: Bitte melden Sie sie im Reiter „Issues“ auf GitHub.

### IPTV-Quellen

- Xtream-Codes-Konten (Server, Benutzername, Passwort), M3U/M3U8-Playlists (Weblink oder lokale Datei) und MAC/Stalker-Portale (Beta).
- Jedes Profil hat eigene Quellen, privat oder mit den anderen Profilen geteilt; nur das Profil, das eine Quelle hinzugefügt hat, kann sie bearbeiten.
- Ein Profil ohne Quelle erhält automatisch die kostenlose Playlist „TV gratuite France“ (rund 230 kostenlose französische Sender aus dem Projekt iptv-org).
- Die Passwörter der Quellen werden mit Ihrem Windows-Konto verschlüsselt.

### Live-TV und Programmführer

- Sender nach Kategorien mit der laufenden Sendung und ihrem Fortschritt; Senderseite mit dem aktuellen und dem nächsten Programm.
- Programmführer (XMLTV) als Raster, von der Quelle oder von einer Adresse Ihrer Wahl.
- Zeitversetztes Fernsehen: anhalten, zurückspulen und wieder zum Live-Bild (standardmäßig 60 Minuten Puffer).
- Lieblingssender und zuletzt gesehene Sender.

### Filme und Serien

- Poster-Raster mit Kategorien, Sortierung und dem Filter Alle / Noch nicht gesehen / Gesehen, dazu ein „Gesehen“-Abzeichen und ein Fortschrittsbalken.
- Detailseiten mit Inhalt, Besetzung, Bewertung und Trailer, ergänzt durch TMDB (kostenloser Schlüssel, in den Einstellungen einzugeben).
- Serien: Staffeln und Folgen mit Vorschaubildern; die nächste Folge startet von selbst, mit Countdown und der Frage „Schauen Sie noch?“.
- Bleiben Sie kurz mit dem Mauszeiger auf einem Poster, um eine Vorschau mit Trailer, Ton-Schaltfläche und Lautstärkeregler zu sehen.
- Die Wiedergabe wird dort fortgesetzt, wo Sie aufgehört haben; Verlauf und Meine Liste für jedes Profil.

### Player

- mpv-Engine: Hardware-Dekodierung, 4K- und HDR-Quellen, Modus „Hohe Bildqualität“ mit Entbänderung von Farbverläufen.
- Flüssige Anzeige, die der Bildwiederholrate folgt, mit automatischem Wechsel in einen kompatiblen Modus auf PCs, auf denen sie nicht funktioniert.
- Tonspur, Untertitel, Audio-Ausgabegerät, Seitenverhältnis, Geschwindigkeit und Lautstärke bis 150 %.
- Rechtsklickmenü auf dem Video und Live-Informationen zur Wiedergabe (Codec, Auflösung, Bitrate, verlorene Bilder…).
- Video ziehen, um das Fenster zu verschieben, Doppelklick für Vollbild, Tastenkürzel (Leertaste, Pfeile, F, M, R…).
- Abgebrochene Live-Streams werden automatisch neu geöffnet.

### Mini-Player

- Das Video läuft in einem schwebenden Fenster weiter, während Sie in der App unterwegs sind.
- Verschieben Sie ihn beliebig und ändern Sie seine Größe über Ränder und Ecken; beim Darüberfahren erscheinen die Bedienelemente (Wiedergabe/Pause, Sprünge, Ton, zurück zum großen Player, Stopp).

### Aufnahmen

- Sofortaufnahme aus dem Player und geplante Aufnahmen aus dem Programmführer.
- Format TS, MKV oder MP4, Qualitätswahl bei Streams mit mehreren Bitraten, frei wählbare Dateinamen, automatischer Stopp am Ende der Sendung.
- Schutzmechanismen: Mindest-Speicherplatz, Begrenzung gleichzeitiger Aufnahmen (weitere warten), PC bleibt wach, Bestätigung vor dem Schließen, Warnung, wenn die Datei nicht mehr wächst.

### Profile und Jugendschutz

- Mehrere Profile, jedes mit Avatar (Emoji oder Bild), Farbe, optionalem Passwort und eigenen Einstellungen (Design, Sprache, Startseite, Verlauf…).
- Kinderprofile: Erwachseneninhalte ausgeblendet, Jugendschutz-PIN nötig, um geschützte Einstellungen oder den Profiltyp zu ändern.
- Gesperrte Kategorien, automatische Sperre von Erwachsenenkategorien, durch die PIN geschützte Einstellungen.
- Profilmenü unten links: Profil bearbeiten, Profil wechseln, abmelden.

### Oberfläche

- Verfügbar in 6 Sprachen: Französisch, Englisch, Spanisch, Deutsch, Italienisch und Portugiesisch.
- Helles und dunkles Design mit wählbarer Farbe; anpassbare Startseite.
- Suche in Sendern, Filmen, Serien und Sendungen (Strg+F).
- Einstellungen in Bereiche gegliedert, mit Suchfeld.

### Installation, Updates und Sicherung

- Installation mit einem Klick, ohne Administratorrechte; eine portable Version ist ebenfalls verfügbar.
- Automatische Updates von GitHub, installiert beim Schließen von MilfTV, damit nichts unterbrochen wird; Kanäle Stabil und Beta, mit jederzeitiger Rückkehr zu Stabil.
- Sicherung und Wiederherstellung der Konfiguration (Profile, Quellen, Einstellungen, Verlauf, Meine Liste…), mit optionalem Passwort für den Umzug auf einen anderen PC.
- Versionshinweise in der App (Einstellungen › Daten und Info › Versionshinweise).

### Bekannte Einschränkungen

- Nur Windows 10 und 11, 64 Bit.
- HDR-Inhalte werden in SDR angezeigt (Tone-Mapping).
- Das Installationsprogramm ist noch nicht signiert: Beim ersten Start kann Windows SmartScreen eine Warnung anzeigen (auf „Weitere Informationen“ und dann „Trotzdem ausführen“ klicken).
- Aufnahmen laufen nur, solange MilfTV geöffnet ist.
