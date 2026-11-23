# MilfTV - Notes de version

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Toutes les modifications notables de MilfTV, la plus récente en premier.

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
