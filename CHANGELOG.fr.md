# MilfTV - Notes de version

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Toutes les modifications notables de MilfTV, la plus récente en premier.

## 1.0.0-beta.2

*Publiée le 2026-11-24*

Mise à jour de sécurité, à la suite d'un audit complet de l'application, avec des nouveautés : informations sur le média dans les fiches et les aperçus, fiche des programmes au survol des chaînes, saison ou série entière marquée comme vue d'un coup, raccourci sur le bureau au choix, et une version native pour les PC Windows sur ARM. Elle est recommandée à tous.

### Nouveautés

- Dans la liste des chaînes du lecteur, laissez la souris un instant sur une chaîne pour voir sa fiche : le programme en cours (horaire, progression, temps restant, genre, épisode, résumé et image quand le guide les fournit) et les programmes suivants. Elle suit le réglage « Aperçu au survol » et son délai, comme les affiches.
- Séries : marquez d'un coup toute une saison ou toute la série comme vue (ou non vue), avec les boutons de la fiche de la série. Le menu d'une affiche de série (clic droit) propose aussi « Marquer toute la série comme vue ». Le bouton de lecture repart alors du premier épisode non vu.
- Installation : le raccourci sur le bureau n'est plus imposé. Au premier lancement, MilfTV vous demande si vous en voulez un, et vous pouvez l'ajouter ou le retirer à tout moment dans Paramètres › Données et à propos. Il est retiré à la désinstallation. Le raccourci du menu Démarrer est toujours créé.
- Accueil : les rangées horizontales se font défiler plus facilement. Leur ascenseur a été redessiné aux couleurs de l'application (une poignée à la couleur d'accent, qui s'épaissit au survol) et reste toujours visible, des flèches apparaissent au survol pour avancer d'un écran, et vous pouvez faire glisser une rangée sur le côté avec la souris (clic gauche maintenu, même sur une affiche). Il en va de même pour Distribution et Dans la même catégorie sur les fiches.
- Mini-lecteur : réglez le volume directement, avec un curseur à côté du bouton du son (et le pourcentage quand la place le permet), ou avec la molette de la souris sur le mini-lecteur.
- Fiches des films et des séries : informations sur le média. Des pastilles sous le titre (4K, HDR, HEVC, 5.1, langues) et une section Informations sur le média : résolution, vidéo, audio, langues audio, sous-titres, durée, débit, format et langue originale. Votre fournisseur les indique, et la première lecture les complète (toutes les langues audio et les sous-titres). Pour les séries, chaque épisode a sa ligne technique et la fiche résume la saison.
- Tous les ascenseurs de l'application adoptent le nouveau style : une piste discrète et une poignée à la couleur d'accent, qui s'épaissit au survol.
- Aperçu au survol des affiches : il montre aussi les informations sur le média (résolution, HDR, codec, son, langues) et, pour les séries, la durée d'un épisode.
- Lecture automatique : choisissez quand démarre le compte à rebours de l'épisode suivant (Paramètres › Lecture automatique) : à la fin de l'épisode, comme avant, ou avant la fin, pendant le générique, pour que l'épisode suivant commence dès la fin. Le compte à rebours peut maintenant durer jusqu'à 60 secondes.
- Windows sur ARM : une version ARM64 native pour les PC à processeur Snapdragon et autres puces ARM, avec son propre programme d'installation et ses propres mises à jour automatiques.

### Sécurité

- Les adresses de flux des playlists et des portails sont désormais vérifiées : les partages réseau et les protocoles internes du moteur vidéo sont refusés. Une playlist malveillante ne peut plus faire envoyer par Windows l'empreinte du mot de passe de votre compte à un serveur inconnu.
- Les certificats HTTPS des serveurs IPTV sont désormais vérifiés. Si votre fournisseur utilise un certificat non valide, cochez « Accepter un certificat non valide » dans les options avancées de la source (déconseillé).
- Les identifiants, mots de passe et jetons sont masqués dans le journal : il peut être joint à un signalement de bug.
- Le code parental est conservé sous forme d'empreinte salée, et les erreurs répétées de code ou de mot de passe de profil imposent désormais une attente croissante (de 5 secondes à 5 minutes).
- Les playlists, catalogues ou guides TV démesurés sont refusés au lieu de saturer la mémoire.
- Le lecteur de bandes-annonces refuse les téléchargements et les demandes d'accès à la caméra, au micro, à la position ou aux notifications.
- Les outils de test ne sont plus inclus dans les versions publiées.
- Avant une restauration, les serveurs des sources de la sauvegarde et, s'il y en a un, son dossier réseau d'enregistrement sont affichés.

### Corrections

- Le titre « Qui regarde ? » suit le changement de langue.
- Dans Ma liste, « Marquer comme vu » ne faisait rien sur une série : tous ses épisodes sont maintenant marqués.

## 1.0.0-beta.1

*Publiée le 2026-11-23*

Première bêta publique de MilfTV, un lecteur IPTV pour Windows. Cette version rassemble tout ce qui a été construit jusqu'ici ; voici le tour complet de ce qu'elle sait faire. Comme toute bêta, elle peut encore contenir des erreurs : signalez-les dans l'onglet Issues sur GitHub.

### Sources IPTV

- Comptes Xtream Codes (serveur, identifiant, mot de passe), playlists M3U/M3U8 (lien web ou fichier) et portails MAC/Stalker (bêta).
- Chaque profil a ses propres sources, privées ou partagées avec les autres profils ; seul le profil qui a ajouté une source peut la modifier.
- Un profil sans aucune source reçoit automatiquement la playlist gratuite « TV gratuite France » (environ 230 chaînes françaises gratuites du projet iptv-org).
- Les mots de passe des sources sont chiffrés avec votre compte Windows.

### TV en direct et guide TV

- Chaînes par catégorie avec le programme en cours et sa progression ; fiche de chaîne avec ce qui passe maintenant et ensuite.
- Guide TV (XMLTV) en grille, fourni par la source ou depuis une adresse de votre choix.
- Direct différé : mettez en pause, revenez en arrière et rattrapez le direct (mémoire de 60 minutes par défaut).
- Chaînes favorites et chaînes regardées récemment.

### Films et séries

- Grilles d'affiches avec catégories, tri et filtre Tout / Pas encore vus / Déjà vus, badge « Vu » et barre de progression.
- Fiches détaillées avec résumé, distribution, note et bande-annonce, enrichies par TMDB (clé gratuite à saisir dans les Paramètres).
- Séries : saisons et épisodes avec vignettes ; l'épisode suivant démarre tout seul, avec compte à rebours et question « Toujours là ? ».
- Laissez le pointeur un instant sur une affiche pour voir un aperçu avec sa bande-annonce, un bouton son et un curseur de volume.
- Reprise de la lecture là où vous vous étiez arrêté ; historique et Ma liste propres à chaque profil.

### Lecteur

- Moteur mpv : décodage matériel, sources 4K et HDR, mode « Image haute qualité » avec atténuation des bandes dans les dégradés.
- Affichage fluide qui suit la fréquence de l'écran, avec retour automatique à un mode compatible sur les PC où il ne fonctionne pas.
- Piste audio, sous-titres, périphérique de sortie audio, format d'image, vitesse et volume jusqu'à 150 %.
- Menu du clic droit sur la vidéo et panneau d'informations de lecture en direct (codec, définition, débit, images perdues…).
- Glissez la vidéo pour déplacer la fenêtre, double-cliquez pour le plein écran, raccourcis clavier (Espace, flèches, F, M, R…).
- Les directs qui coupent sont relancés automatiquement.

### Mini-lecteur

- La vidéo continue dans une fenêtre flottante pendant que vous naviguez dans l'application.
- Déplacez-le où vous voulez et redimensionnez-le par ses bords et ses coins ; au survol, ses commandes apparaissent (lecture/pause, sauts, son, retour au lecteur complet, arrêt).

### Enregistrements

- Enregistrement immédiat depuis le lecteur et enregistrements programmés depuis le guide TV.
- Format TS, MKV ou MP4, choix de la qualité pour les flux multi-débits, nom des fichiers à votre goût, arrêt automatique à la fin du programme.
- Garde-fous : espace disque minimal, limite d'enregistrements simultanés (les suivants attendent leur tour), PC maintenu éveillé, confirmation avant de fermer, alerte si le fichier n'avance plus.

### Profils et contrôle parental

- Plusieurs profils, chacun avec son avatar (émoji ou image), sa couleur, un mot de passe facultatif et ses préférences (thème, langue, tableau de bord, historique…).
- Profils enfants : contenus adultes masqués, code parental exigé pour modifier les réglages protégés ou le type de profil.
- Catégories verrouillées, verrouillage automatique des catégories adultes, paramètres protégés par le code.
- Menu du profil en bas à gauche : modifier le profil, changer de profil, se déconnecter.

### Interface

- Disponible en 6 langues : français, anglais, espagnol, allemand, italien et portugais.
- Thèmes clair et sombre avec couleur au choix ; tableau de bord de l'accueil personnalisable.
- Recherche dans les chaînes, films, séries et programmes (Ctrl+F).
- Paramètres organisés en rubriques, avec une recherche.

### Installation, mises à jour et sauvegarde

- Programme d'installation en un clic, sans droits administrateur ; une version portable est aussi disponible.
- Mises à jour automatiques depuis GitHub, installées à la fermeture de MilfTV pour ne rien interrompre ; canaux Stable et Bêta, avec retour au Stable à tout moment.
- Sauvegarde et restauration de la configuration (profils, sources, réglages, historique, Ma liste…), avec un mot de passe facultatif pour la transférer sur un autre PC.
- Notes de version dans l'application (Paramètres › Données et à propos › Notes de version).

### Limites connues

- Windows 10 et 11 en 64 bits uniquement.
- Les contenus HDR sont affichés en SDR (conversion des tons).
- Le programme d'installation n'est pas encore signé : au premier lancement, Windows SmartScreen peut afficher un avertissement (cliquez sur « Informations complémentaires », puis « Exécuter quand même »).
- Les enregistrements ne se font que si MilfTV est ouvert.
