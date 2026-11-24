# MilfTV - Versionshinweise

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Alle wichtigen Änderungen an MilfTV, die neueste zuerst.

## 1.0.0-beta.2

*Veröffentlicht am 2026-11-24*

Sicherheitsupdate nach einer vollständigen Prüfung der Anwendung, mit Neuerungen: Medieninformationen auf Detailseiten und in Vorschauen, Programmübersicht beim Überfahren der Sender, ganze Staffel oder Serie auf einmal als gesehen markieren, Desktop-Verknüpfung nach Wahl und eine native Version für Windows-on-ARM-PCs. Es wird allen empfohlen.

### Neu

- Bleiben Sie in der Senderliste des Players kurz mit der Maus auf einem Sender, um seine Übersicht zu sehen: die laufende Sendung (Uhrzeit, Fortschritt, Restzeit, Genre, Folge, Inhalt und Bild, wenn der Programmführer sie liefert) und die nächsten Sendungen. Sie folgt der Einstellung „Vorschau beim Überfahren“ und ihrer Verzögerung, wie bei den Postern.
- Serien: Markieren Sie eine ganze Staffel oder die ganze Serie auf einmal als gesehen (oder nicht gesehen), mit den Schaltflächen auf der Serienseite. Das Kontextmenü eines Serienposters bietet außerdem „Ganze Serie als gesehen markieren“. Die Wiedergabe-Schaltfläche beginnt dann mit der ersten nicht gesehenen Folge.
- Installation: Die Desktop-Verknüpfung wird nicht mehr erzwungen. Beim ersten Start fragt MilfTV, ob Sie eine möchten, und Sie können sie jederzeit unter Einstellungen › Daten und Info hinzufügen oder entfernen. Beim Deinstallieren wird sie entfernt. Die Verknüpfung im Startmenü wird weiterhin angelegt.
- Startseite: Die horizontalen Reihen lassen sich leichter blättern. Ihre Bildlaufleiste wurde in den Farben der Anwendung neu gestaltet (ein Griff in der Akzentfarbe, der beim Überfahren breiter wird) und ist immer sichtbar, beim Überfahren erscheinen Pfeile, um eine Bildschirmbreite weiterzugehen, und Sie können eine Reihe mit der Maus seitlich ziehen (linke Taste gedrückt halten, auch auf einem Poster). Dasselbe gilt für Besetzung und In derselben Kategorie auf den Detailseiten.
- Mini-Player: Stellen Sie die Lautstärke direkt ein, mit einem Schieberegler neben der Ton-Schaltfläche (und der Prozentangabe, wenn genug Platz ist) oder mit dem Mausrad über dem Mini-Player.
- Film- und Serienseiten: Medieninformationen. Markierungen unter dem Titel (4K, HDR, HEVC, 5.1, Sprachen) und ein Abschnitt Medieninformationen: Auflösung, Video, Audio, Audiosprachen, Untertitel, Dauer, Bitrate, Format und Originalsprache. Ihr Anbieter liefert sie, und die erste Wiedergabe ergänzt sie (alle Audiosprachen und Untertitel). Bei Serien hat jede Folge ihre technische Zeile und die Seite fasst die Staffel zusammen.
- Alle Bildlaufleisten der Anwendung haben den neuen Stil: eine dezente Spur und ein Griff in der Akzentfarbe, der beim Überfahren breiter wird.
- Vorschau beim Überfahren der Poster: Sie zeigt jetzt auch die Medieninformationen (Auflösung, HDR, Codec, Ton, Sprachen) und bei Serien die Länge einer Folge.
- Automatische Wiedergabe: Wählen Sie, wann der Countdown zur nächsten Folge beginnt (Einstellungen › Automatische Wiedergabe): am Ende der Folge, wie bisher, oder vor dem Ende, während des Abspanns, damit die nächste Folge direkt am Ende startet. Der Countdown kann jetzt bis zu 60 Sekunden dauern.
- Windows on ARM: eine native ARM64-Version für PCs mit Snapdragon-Prozessoren und anderen ARM-Chips, mit eigenem Installationsprogramm und eigenen automatischen Updates.

### Sicherheit

- Stream-Adressen aus Playlists und Portalen werden jetzt geprüft: Netzwerkfreigaben und interne Protokolle der Video-Engine werden abgelehnt. Eine bösartige Playlist kann Windows nicht mehr dazu bringen, einen Hash Ihres Kontopassworts an einen unbekannten Server zu senden.
- HTTPS-Zertifikate von IPTV-Servern werden jetzt überprüft. Verwendet Ihr Anbieter ein ungültiges Zertifikat, aktivieren Sie „Ungültiges Zertifikat akzeptieren“ in den erweiterten Optionen der Quelle (nicht empfohlen).
- Benutzernamen, Passwörter und Tokens werden im Protokoll maskiert, sodass es einer Fehlermeldung beigefügt werden kann.
- Die Jugendschutz-PIN wird als gesalzener Hash gespeichert, und wiederholte falsche PINs oder Profilpasswörter erzwingen jetzt eine zunehmende Wartezeit (von 5 Sekunden bis 5 Minuten).
- Übergroße Playlists, Kataloge oder Programmführer werden abgelehnt, statt den Speicher zu füllen.
- Der Trailer-Player lehnt Downloads sowie Zugriffe auf Kamera, Mikrofon, Standort oder Benachrichtigungen ab.
- Testwerkzeuge sind in veröffentlichten Versionen nicht mehr enthalten.
- Vor einer Wiederherstellung werden die Quellserver der Sicherung und gegebenenfalls ihr Netzwerkordner für Aufnahmen angezeigt.

### Korrekturen

- Der Titel „Wer schaut zu?“ folgt einem Sprachwechsel.
- In Meine Liste bewirkte „Als gesehen markieren“ bei einer Serie nichts: Jetzt werden alle ihre Folgen markiert.

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
