# MilfTV - Release notes

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

All notable changes to MilfTV, newest first.

## 1.0.0-beta.2

*Released on 2026-11-24*

Security update following a full audit of the application, with new features: media information on detail pages and previews, programme sheet when hovering channels, whole season or series marked as watched in one go, an optional desktop shortcut, and a native version for Windows on ARM PCs. Updating is recommended for everyone.

### New

- In the player's channel list, leave the mouse on a channel for a moment to see its programme sheet: what is on now (times, progress, time left, genre, episode, synopsis and picture when the guide provides them) and the next programmes. It follows the "Hover preview" setting and its delay, like the posters.
- Series: mark a whole season or the whole series as watched (or unwatched) in one go, with the buttons on the series page. The right-click menu of a series poster also offers "Mark the whole series as watched". The play button then starts from the first unwatched episode.
- Installation: the desktop shortcut is no longer forced. On first launch, MilfTV asks whether you want one, and you can add or remove it at any time in Settings › Data and about. It is removed when MilfTV is uninstalled. The Start menu shortcut is still created.
- Home: the horizontal rows are easier to scroll. Their scroll bar has been redesigned in the app's colours (a handle in the accent colour that thickens on hover) and is always shown, arrows appear on hover to move one screen at a time, and you can drag a row sideways with the mouse (hold the left button, even on a poster). The same goes for Cast and In the same category on the detail pages.
- Mini player: set the volume right there, with a slider next to the sound button (and the percentage when there is room), or with the mouse wheel over the mini player.
- Movie and series pages: media information. Badges under the title (4K, HDR, HEVC, 5.1, languages) and a Media information section: resolution, video, audio, audio languages, subtitles, duration, bitrate, format and original language. Your provider supplies them, and the first playback completes them (all audio languages and subtitles). On series, each episode gets its own technical line and the page sums up the season.
- All the scroll bars in the app now share the new look: a discreet track and a handle in the accent colour that thickens on hover.
- Poster hover preview: it also shows the media information (resolution, HDR, codec, sound, languages) and, for series, the length of an episode.
- Autoplay: choose when the next-episode countdown starts (Settings › Autoplay): at the end of the episode, as before, or before the end, during the credits, so that the next episode begins as soon as this one ends. The countdown can now last up to 60 seconds.
- Windows on ARM: a native ARM64 version for PCs with Snapdragon processors and other ARM chips, with its own installer and its own automatic updates.

### Security

- Stream addresses from playlists and portals are now checked: network shares and the video engine's internal protocols are refused. A malicious playlist can no longer make Windows send a hash of your account password to an unknown server.
- HTTPS certificates of IPTV servers are now verified. If your provider uses an invalid certificate, tick "Accept an invalid certificate" in the source's advanced options (not recommended).
- Usernames, passwords and tokens are masked in the log, so it can be attached to a bug report.
- The parental PIN is stored with a salted hash, and repeated wrong PINs or profile passwords now impose an increasing wait (from 5 seconds to 5 minutes).
- Oversized playlists, catalogues or TV guides are rejected instead of filling the memory.
- The trailer player refuses downloads and requests for the camera, microphone, location or notifications.
- Test tools are no longer included in published versions.
- Before a restore, the backup's source servers and, if any, its network recordings folder are shown.

### Fixes

- The "Who's watching?" title follows a change of language.
- In My List, "Mark as watched" did nothing on a series: it now marks all its episodes.

## 1.0.0-beta.1

*Released on 2026-11-23*

First public beta of MilfTV, an IPTV player for Windows. This version brings together everything built so far; here is a complete tour of what it can do. As a beta, it may still contain bugs: please report them in the Issues tab on GitHub.

### IPTV sources

- Xtream Codes accounts (server, username, password), M3U/M3U8 playlists (web link or local file) and MAC/Stalker portals (beta).
- Each profile has its own sources, private or shared with the other profiles; only the profile that added a source can edit it.
- A profile without any source automatically gets the free "TV gratuite France" playlist (about 230 free French channels from the iptv-org project).
- Source passwords are encrypted with your Windows account.

### Live TV and TV guide

- Channels by category, with the current programme and its progress; channel page with what's on now and next.
- TV guide (XMLTV) as a grid, from the source or from an address of your choice.
- Timeshift: pause, rewind and catch up with live (60-minute buffer by default).
- Favourite channels and recently watched channels.

### Films and series

- Poster grids with categories, sorting and an All / Not watched yet / Watched filter, plus a "Watched" badge and a progress bar.
- Detail pages with synopsis, cast, rating and trailer, enriched by TMDB (free key to enter in Settings).
- Series: seasons and episodes with thumbnails; the next episode starts on its own, with a countdown and a "Still watching?" check.
- Hover a poster for a moment to see a preview with its trailer, a sound button and a volume slider.
- Playback resumes where you left off; history and My List for each profile.

### Player

- mpv engine: hardware decoding, 4K and HDR sources, "High quality picture" mode with gradient debanding.
- Smooth display that follows the screen refresh rate, with an automatic fallback to a compatible mode on PCs where it does not work.
- Audio track, subtitles, audio output device, aspect ratio, speed and volume up to 150 %.
- Right-click menu on the video and a live playback information panel (codec, resolution, bitrate, dropped frames…).
- Drag the video to move the window, double-click for full screen, keyboard shortcuts (Space, arrows, F, M, R…).
- Live streams that drop are reopened automatically.

### Mini player

- The video keeps playing in a floating window while you browse the app.
- Move it anywhere and resize it from its edges and corners; hover it to show the controls (play/pause, jumps, sound, back to full player, stop).

### Recordings

- Instant recording from the player and scheduled recordings from the TV guide.
- TS, MKV or MP4 format, quality choice for multi-bitrate streams, file names you define, automatic stop at the end of the programme.
- Safeguards: minimum free disk space, limit of simultaneous recordings (the next ones wait their turn), PC kept awake, confirmation before closing, alert when the file stops growing.

### Profiles and parental control

- Several profiles, each with an avatar (emoji or picture), a colour, an optional password and its own preferences (theme, language, home dashboard, history…).
- Kids profiles: adult content hidden, parental PIN required to change protected settings or the profile type.
- Locked categories, automatic locking of adult categories, settings protected by the PIN.
- Profile menu at the bottom left: edit the profile, switch profile, sign out.

### Interface

- Available in 6 languages: French, English, Spanish, German, Italian and Portuguese.
- Light and dark themes with a choice of colour; customisable home dashboard.
- Search across channels, films, series and programmes (Ctrl+F).
- Settings organised in sections, with a search box.

### Installation, updates and backup

- One-click installer, without administrator rights; a portable version is also available.
- Automatic updates from GitHub, installed when MilfTV closes so nothing is interrupted; Stable and Beta channels, with a way back to Stable at any time.
- Backup and restore of the configuration (profiles, sources, settings, history, My List…), with an optional password to move it to another PC.
- Release notes in the app (Settings › Data and about › Release notes).

### Known limitations

- Windows 10 and 11, 64-bit only.
- HDR content is displayed in SDR (tone-mapped).
- The installer is not signed yet: on first launch, Windows SmartScreen may show a warning (click "More info", then "Run anyway").
- Recordings only run while MilfTV is open.
