# MilfTV - Note di rilascio

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Tutte le modifiche rilevanti di MilfTV, dalla più recente.

## 1.0.0-beta.1

*Pubblicata il 2026-11-23*

Prima beta pubblica di MilfTV, un lettore IPTV per Windows. Questa versione riunisce tutto ciò che è stato realizzato finora; ecco una panoramica completa di ciò che sa fare. Come ogni beta, può ancora contenere errori: segnalali nella scheda Issues su GitHub.

### Sorgenti IPTV

- Account Xtream Codes (server, nome utente, password), playlist M3U/M3U8 (link web o file locale) e portali MAC/Stalker (beta).
- Ogni profilo ha le proprie sorgenti, private o condivise con gli altri profili; solo il profilo che ha aggiunto una sorgente può modificarla.
- Un profilo senza sorgenti riceve automaticamente la playlist gratuita «TV gratuite France» (circa 230 canali francesi gratuiti del progetto iptv-org).
- Le password delle sorgenti sono cifrate con il tuo account Windows.

### TV in diretta e guida TV

- Canali per categoria con il programma in corso e il suo avanzamento; scheda del canale con ciò che va in onda ora e dopo.
- Guida TV (XMLTV) a griglia, fornita dalla sorgente o da un indirizzo a tua scelta.
- Diretta differita: metti in pausa, torna indietro e raggiungi la diretta (memoria di 60 minuti per impostazione predefinita).
- Canali preferiti e canali visti di recente.

### Film e serie

- Griglie di locandine con categorie, ordinamento e filtro Tutti / Non ancora visti / Visti, più un badge «Visto» e una barra di avanzamento.
- Schede con trama, cast, voto e trailer, arricchite da TMDB (chiave gratuita da inserire nelle Impostazioni).
- Serie: stagioni ed episodi con miniature; l'episodio successivo parte da solo, con conto alla rovescia e domanda «Sei ancora lì?».
- Lascia il puntatore un attimo su una locandina per vedere un'anteprima con il trailer, un pulsante audio e un cursore del volume.
- La riproduzione riprende da dove l'avevi lasciata; cronologia e La mia lista per ogni profilo.

### Lettore

- Motore mpv: decodifica hardware, sorgenti 4K e HDR, modalità «Immagine di alta qualità» con riduzione delle bande nelle sfumature.
- Visualizzazione fluida che segue la frequenza dello schermo, con ritorno automatico a una modalità compatibile sui PC dove non funziona.
- Traccia audio, sottotitoli, dispositivo di uscita audio, formato dell'immagine, velocità e volume fino al 150 %.
- Menu del clic destro sul video e pannello di informazioni sulla riproduzione in tempo reale (codec, risoluzione, bitrate, fotogrammi persi…).
- Trascina il video per spostare la finestra, doppio clic per lo schermo intero, scorciatoie da tastiera (Spazio, frecce, F, M, R…).
- Le dirette che si interrompono vengono riaperte automaticamente.

### Mini lettore

- Il video continua in una finestra mobile mentre navighi nell'app.
- Spostalo dove vuoi e ridimensionalo dai bordi e dagli angoli; passandoci sopra compaiono i comandi (riproduci/pausa, salti, audio, torna al lettore completo, ferma).

### Registrazioni

- Registrazione immediata dal lettore e registrazioni programmate dalla guida TV.
- Formato TS, MKV o MP4, scelta della qualità per i flussi a più bitrate, nomi dei file a tuo piacimento, arresto automatico a fine programma.
- Protezioni: spazio minimo su disco, limite di registrazioni simultanee (le successive attendono il loro turno), PC tenuto attivo, conferma prima di chiudere, avviso se il file smette di crescere.

### Profili e controllo parentale

- Più profili, ognuno con avatar (emoji o immagine), colore, password facoltativa e preferenze proprie (tema, lingua, schermata iniziale, cronologia…).
- Profili bambini: contenuti per adulti nascosti, codice parentale richiesto per modificare le impostazioni protette o il tipo di profilo.
- Categorie bloccate, blocco automatico delle categorie per adulti, impostazioni protette dal codice.
- Menu del profilo in basso a sinistra: modifica il profilo, cambia profilo, esci.

### Interfaccia

- Disponibile in 6 lingue: francese, inglese, spagnolo, tedesco, italiano e portoghese.
- Temi chiaro e scuro con colore a scelta; schermata iniziale personalizzabile.
- Ricerca tra canali, film, serie e programmi (Ctrl+F).
- Impostazioni organizzate in sezioni, con casella di ricerca.

### Installazione, aggiornamenti e backup

- Installazione in un clic, senza diritti di amministratore; è disponibile anche una versione portatile.
- Aggiornamenti automatici da GitHub, installati alla chiusura di MilfTV per non interrompere nulla; canali Stabile e Beta, con ritorno a Stabile in qualsiasi momento.
- Backup e ripristino della configurazione (profili, sorgenti, impostazioni, cronologia, La mia lista…), con password facoltativa per trasferirla su un altro PC.
- Note di rilascio nell'app (Impostazioni › Dati e informazioni › Note di rilascio).

### Limiti noti

- Solo Windows 10 e 11 a 64 bit.
- I contenuti HDR sono visualizzati in SDR (con conversione dei toni).
- Il programma di installazione non è ancora firmato: al primo avvio Windows SmartScreen può mostrare un avviso (fai clic su «Ulteriori informazioni», poi su «Esegui comunque»).
- Le registrazioni avvengono solo mentre MilfTV è aperto.
