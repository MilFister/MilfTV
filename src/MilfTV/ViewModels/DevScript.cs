using Avalonia.Threading;
using MilfTV.Core;
using MilfTV.Theming;

namespace MilfTV.ViewModels;

/// <summary>Scénario de test piloté par la variable d'environnement MILFTV_DEV
/// (ex. "wait=3000;nav=live;play=0;wait=8000;close"). Sans effet en utilisation normale :
/// il ne s'exécute qu'avec un dossier de données isolé (MILFTV_DATA), jamais sur les vraies données.</summary>
public static class DevScript
{
    /// <summary>Script présent ET données isolées : seule configuration où les tests peuvent s'exécuter.</summary>
    /// <summary>Outils de test compilés dans cette version : oui pour le développement, non pour les versions publiées
    /// (build/release.ps1 sans -DevTools) : rien ne peut alors être piloté par des variables d'environnement.</summary>
#if DEVTOOLS
    public static readonly bool ToolsCompiled = true;
#else
    public static readonly bool ToolsCompiled = false;
#endif

    public static bool IsEnabled =>
        ToolsCompiled && AppPaths.DevRoot is not null && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MILFTV_DEV"));

    public static void TryRun(MainViewModel main)
    {
        if (!IsEnabled) return;
        var script = Environment.GetEnvironmentVariable("MILFTV_DEV")!;
        var started = false;
        void Start()
        {
            // « now; » en tête : démarrage immédiat, sans attendre le catalogue (écran des profils, premier lancement).
            if (started || (Library.Instance.Catalog is null && !script.StartsWith("now;"))) return;
            started = true;
            Dispatcher.UIThread.Post(async () => await RunAsync(main, script));
        }
        Library.Instance.CatalogChanged += Start;
        Start();
    }

    private static async Task RunAsync(MainViewModel main, string script)
    {
        Log.Info("DevScript : " + script);
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('=', 2);
            var cmd = parts[0].Trim();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";
            try
            {
                var c = Library.Instance.Catalog!;
                switch (cmd)
                {
                    case "wait": await Task.Delay(int.Parse(arg)); break;
                    case "nav": main.Navigate(arg); break;
                    case "back": main.GoBack(); break;
                    case "theme":
                        var t = arg.Split(',');
                        ThemeService.Apply(t[0], t.Length > 1 ? t[1] : ThemeService.Current.Id);
                        break;
                    case "play":
                        var ch = int.TryParse(arg, out var i) ? c.Channels[i] : c.Channels.First(x => x.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
                        await main.PlayChannelAsync(ch, null);
                        break;
                    case "movie": main.OpenDetails(c.Movies[int.Parse(arg)]); break;
                    case "series": main.OpenDetails(c.Series[int.Parse(arg)]); break;
                    case "playmovie": await main.PlayMovieAsync(c.Movies[int.Parse(arg)]); break;
                    case "search": main.SearchText = arg; break;
                    case "fullscreen": main.IsFullscreen = !main.IsFullscreen; break;
                    case "pause": main.Player.TogglePauseCommand.Execute(null); break;
                    case "seek": main.Player.SeekByCommand.Execute(arg); break;
                    case "live": main.Player.GoLiveCommand.Execute(null); break;
                    case "panel": main.Player.ToggleSidePanelCommand.Execute(null); break;
                    case "tracks": main.Player.ToggleTracksPanelCommand.Execute(null); break;
                    case "lang": LanguageChoice.Apply(LanguageChoice.Options.First(o => o.Code == arg)); break;
                    case "hittest":
                        var xy = arg.Split(',');
                        var win = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (win is null) break;
                        var hit = Avalonia.Input.InputExtensions.InputHitTest(win, new Avalonia.Point(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture))) as Avalonia.Visual;
                        var chain = new List<string>();
                        for (var v = hit; v is not null && chain.Count < 12; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                            chain.Add(v.GetType().Name + (v is Avalonia.Controls.Control { Name: { Length: > 0 } n } ? "#" + n : "") + (v is Avalonia.StyledElement { DataContext: Core.LiveChannel lc } ? "[" + lc.Name + "]" : ""));
                        Log.Info("DevScript hittest " + arg + " : " + string.Join(" < ", chain));
                        break;
                    case "hover":
                        // Survol simulé dans l'appli (le vrai curseur ne bouge pas) : mouvement de pointeur brut à x,y.
                        var hxy = arg.Split(',');
                        var hwin = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (hwin?.PlatformImpl is { } himpl)
                        {
                            // API internes d'Avalonia, atteintes par réflexion (test uniquement).
                            const System.Reflection.BindingFlags HF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                            var ht = himpl.GetType();
                            var hinputProp = ht.GetProperty("Input", HF) ?? ht.GetInterfaces().Select(i => i.GetProperty("Input", HF)).FirstOrDefault(p => p is not null);
                            var hinput = hinputProp?.GetValue(himpl) as Action<Avalonia.Input.Raw.RawInputEventArgs>;
                            object? hdev = null;
                            for (var tt = ht; tt is not null && hdev is null; tt = tt.BaseType)
                                hdev = tt.GetFields(HF).Where(f => typeof(Avalonia.Input.IPointerDevice).IsAssignableFrom(f.FieldType)).Select(f => f.GetValue(himpl)).FirstOrDefault(v => v is Avalonia.Input.MouseDevice);
                            var hctor = typeof(Avalonia.Input.Raw.RawPointerEventArgs).GetConstructors(HF)
                                .First(c => c.GetParameters().Length == 6 && c.GetParameters()[4].ParameterType == typeof(Avalonia.Point));
                            var hpt = new Avalonia.Point(double.Parse(hxy[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(hxy[1], System.Globalization.CultureInfo.InvariantCulture));
                            Log.Info($"DevScript survol : entrée={(hinput is not null)} périphérique={hdev?.GetType().Name}");
                            if (hinput is null || hdev is null) break;
                            hinput((Avalonia.Input.Raw.RawInputEventArgs)hctor.Invoke(new object[] { hdev, (ulong)Environment.TickCount64, hwin,
                                Avalonia.Input.Raw.RawPointerEventType.Move, hpt, Avalonia.Input.RawInputModifiers.None }));
                            if (hxy.Length > 2 && hxy[2] == "click")
                                foreach (var (btn, mods) in new[] { (Avalonia.Input.Raw.RawPointerEventType.LeftButtonDown, Avalonia.Input.RawInputModifiers.LeftMouseButton),
                                                                   (Avalonia.Input.Raw.RawPointerEventType.LeftButtonUp, Avalonia.Input.RawInputModifiers.None) })
                                    hinput((Avalonia.Input.Raw.RawInputEventArgs)hctor.Invoke(new object[] { hdev, (ulong)Environment.TickCount64, hwin, btn, hpt, mods }));
                            await Task.Delay(400);
                            var hov = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(hwin).OfType<Avalonia.Controls.Panel>().FirstOrDefault(p => p.Name == "Hud");
                            var ov = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(hwin).OfType<Avalonia.Controls.Control>().FirstOrDefault(p => p.Name == "MiniOverlay");
                            Log.Info($"DevScript survol {arg} : mini survolé={ov?.IsPointerOver} commandes opacité={hov?.Opacity}");
                        }
                        break;
                    case "listbounds":
                        var w2 = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (w2 is null) break;
                        foreach (var lbi in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w2).OfType<Avalonia.Controls.ListBoxItem>()
                                     .Where(x => x.DataContext is Core.LiveChannel && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(x).OfType<Avalonia.Controls.Control>().Any(a => a.Name == arg)).Take(3))
                        {
                            var p0 = Avalonia.VisualExtensions.TranslatePoint(lbi, new Avalonia.Point(0, 0), w2);
                            var hitc = p0 is { } pp ? Avalonia.Input.InputExtensions.InputHitTest(w2, new Avalonia.Point(pp.X + 60, pp.Y + lbi.Bounds.Height / 2)) : null;
                            Log.Info($"DevScript listbounds {((Core.LiveChannel)lbi.DataContext!).Name} : {p0} {lbi.Bounds.Size} visible={lbi.IsEffectivelyVisible} enabled={lbi.IsEffectivelyEnabled} hit={hitc?.GetType().Name}");
                        }
                        break;
                    case "profile": _ = main.SelectProfileAsync(Data.SettingsStore.Current.Profiles[int.Parse(arg)]); break;
                    case "addprofile": main.SaveProfile(null, arg, Data.SettingsStore.ProfileColors[Data.SettingsStore.Current.Profiles.Count % 10], false, null, false); break;
                    case "picker": main.ShowProfilePicker(arg.Length > 0 ? arg : null); break;
                    case "pickerclose": main.CloseProfilePicker(); break;
                    case "tab": if (main.CurrentPage is FavoritesViewModel fv) fv.Tab = int.Parse(arg); break;
                    case "section": if (main.CurrentPage is SettingsViewModel ssv) ssv.SelectSectionCommand.Execute(arg); break;
                    case "settingsfilter": if (main.CurrentPage is SettingsViewModel sf) sf.NavFilter = arg; break;
                    case "scroll":
                        var mw2 = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var sv = mw2 is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(mw2).OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault(s => s.Name == "MainScroll" && s.IsEffectivelyVisible);
                        if (sv is not null) sv.Offset = new Avalonia.Vector(0, double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "addlist": main.Lib.ToggleFavorite(c.Movies[int.Parse(arg)]); break;
                    case "record": main.Player.ToggleRecordCommand.Execute(null); break;
                    case "schedrec":
                        var rc = c.Channels[int.Parse(arg)];
                        main.Recorder.Schedule(rc, "Test programmé", DateTimeOffset.Now.AddMinutes(Data.SettingsStore.Current.RecordingMarginBeforeMin), DateTimeOffset.Now.AddSeconds(25).AddMinutes(-Data.SettingsStore.Current.RecordingMarginAfterMin));
                        break;
                    case "pin": _ = main.RequestPinAsync(); break;
                    case "digit": (main.Dialog as PinDialogViewModel)?.Digit(arg); break;
                    case "activity": main.Player.NotifyActivity(); break;
                    case "close": main.Player.Close(); break;
                    case "addsource": main.AddSource(arg); break;
                    case "advanced": if (main.Dialog is SourceEditorViewModel sev) sev.ShowAdvanced = true; break;
                    case "dialogclose": main.CloseDialog(); break;
                    case "log":
                        var p = main.Player;
                        Log.Info($"DevScript état : open={p.IsOpen} live={p.IsLive} paused={p.IsPaused} pos={p.Position:0.0} range={p.RangeStart:0.0}-{p.RangeEnd:0.0} edge={p.IsAtLiveEdge} behind={p.BehindLiveText} err={p.ErrorMessage} info={p.VideoInfo} hw={p.Engine.HwDecodeCurrent} buffering={p.IsBuffering} engPause={p.Engine.Paused} mpvPause={p.Engine.GetString("pause")} idle={p.Engine.CoreIdle} pfc={p.Engine.PausedForCache}");
                        break;
                    case "shot":
                        // Capture de la fenêtre (rendu Avalonia, sans la vidéo) dans le dossier de données de test.
                        var ws = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (ws is null || AppPaths.DevRoot is not { } devRoot) break;
                        var scale = ws.RenderScaling;
                        var size = new Avalonia.PixelSize((int)(ws.Bounds.Width * scale), (int)(ws.Bounds.Height * scale));
                        using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96 * scale, 96 * scale)))
                        {
                            rtb.Render(ws);
                            rtb.Save(Path.Combine(devRoot, arg + ".png"));
                        }
                        break;
                    case "size":
                        var wz = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (wz is null) break;
                        var wh = arg.Split(',');
                        wz.WindowState = Avalonia.Controls.WindowState.Normal;
                        wz.Width = double.Parse(wh[0], System.Globalization.CultureInfo.InvariantCulture);
                        wz.Height = double.Parse(wh[1], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "dashhide": if (main.CurrentPage is SettingsViewModel d1) d1.DashRows[int.Parse(arg)].IsVisible = false; break;
                    case "dashdown": if (main.CurrentPage is SettingsViewModel d2) d2.MoveRowDownCommand.Execute(d2.DashRows[int.Parse(arg)]); break;
                    case "dashreset": if (main.CurrentPage is SettingsViewModel d3) d3.ResetDashboardCommand.Execute(null); break;
                    case "setpwd":
                        var pw = arg.Split(',', 2);
                        Data.SettingsStore.Current.Profiles[int.Parse(pw[0])].PasswordHash = pw[1].Length > 0 ? Secret.HashPassword(pw[1]) : null;
                        Data.SettingsStore.Save();
                        break;
                    case "pwd":
                        if (main.Dialog is PasswordDialogViewModel pd) { pd.Password = arg; pd.ConfirmCommand.Execute(null); }
                        break;
                    case "logstate":
                        Log.Info($"DevScript profil={main.Lib.Profile?.Name} source={main.Lib.Source?.Name} page={main.CurrentPage?.GetType().Name} dialog={main.Dialog?.GetType().Name} overlay={main.ProfileOverlay?.GetType().Name} sources=[{string.Join(", ", main.Sources.Select(s => s.Name))}] theme={ThemeService.Current.Id}/{Data.SettingsStore.Current.ThemeMode}");
                        break;
                    case "playep":
                        // Ouvre la fiche de la série, attend ses saisons puis lance le premier épisode.
                        main.OpenDetails(c.Series[int.Parse(arg)]);
                        for (var k = 0; k < 80 && (main.CurrentPage as SeriesDetailViewModel)?.HasSeasons != true; k++) await Task.Delay(100);
                        if (main.CurrentPage is SeriesDetailViewModel sdv && sdv.Seasons.FirstOrDefault(s => s.Episodes.Count > 0) is { } sea)
                            await main.PlayEpisodeAsync(sdv.Series, sea.Episodes[0], sea.Episodes);
                        break;
                    case "mini": main.Player.EnterMini(); break;
                    case "leave": main.Player.Leave(); break;
                    case "expand": main.Player.ExpandMini(); break;
                    case "closetrailer": main.CloseTrailer(); break;
                    // ── Profils enfants et code parental ──
                    case "pinset":
                        Data.SettingsStore.Current.ParentalPinHash = arg.Length > 0 ? Secret.HashPin(arg) : null;
                        Library.Instance.RefreshParental();
                        break;
                    case "pincode":
                        foreach (var dg in arg) (main.Dialog as PinDialogViewModel)?.Digit(dg.ToString());
                        break;
                    case "editprofile": main.ShowDialog(new ProfileEditorViewModel(main, Data.SettingsStore.Current.Profiles[int.Parse(arg)])); break;
                    case "editkid":
                        if (main.Dialog is ProfileEditorViewModel ped) { ped.IsKid = arg == "1"; _ = ped.SaveCommand.ExecuteAsync(null); }
                        break;
                    // ── Menu du profil ──
                    case "profilemenu":
                        ((Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as Views.MainWindow)?.OpenProfileMenu();
                        break;
                    case "menuitem":
                        // edit, avatar, settings, add, manage, logout, switch:i — comme un clic dans le menu du profil.
                        var pm = new ProfileMenuViewModel(main);
                        switch (arg.Split(':')[0])
                        {
                            case "edit": pm.EditCommand.Execute(null); break;
                            case "avatar": pm.EditAvatarCommand.Execute(null); break;
                            case "settings": pm.SettingsCommand.Execute(null); break;
                            case "add": pm.AddCommand.Execute(null); break;
                            case "manage": pm.ManageCommand.Execute(null); break;
                            case "logout": pm.LogOutCommand.Execute(null); break;
                            case "switch": pm.SwitchCommand.Execute(pm.Others[int.Parse(arg.Split(':')[1])]); break;
                        }
                        break;
                    case "logprofile":
                        Log.Info($"DevScript profil : actif={Library.Instance.Profile?.Name} écran={main.ProfileOverlay?.GetType().Name} fermable={main.ProfileOverlay?.CanClose} déconnecté={main.ProfileOverlay?.LoggedOut} "
                            + $"dialogue={main.Dialog?.GetType().Name} page={main.CurrentPage?.GetType().Name} section={(main.CurrentPage as SettingsViewModel)?.Section} lecteur={main.Player.IsOpen}");
                        break;
                    // ── Avatars ──
                    case "avatar":
                        // « #n » : n-ième choix de la galerie (0 = initiale), sinon valeur exacte.
                        if (main.Dialog is ProfileEditorViewModel pav && (arg.StartsWith('#') ? pav.AvatarChoices.ElementAtOrDefault(int.Parse(arg[1..])) : pav.AvatarChoices.FirstOrDefault(a => a.Value == (arg.Length == 0 ? null : arg))) is { } choice)
                            pav.PickAvatarCommand.Execute(choice);
                        break;
                    case "avatarimg":
                        if (main.Dialog is ProfileEditorViewModel pai)
                        {
                            using var fsimg = System.IO.File.OpenRead(arg);
                            if (Controls.ProfileAvatars.Import(fsimg) is { } imported) pai.SetImageAvatar(imported);
                        }
                        break;
                    case "avatarflyout":
                        var wav = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var pev = wav is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wav).OfType<Views.ProfileEditorView>().FirstOrDefault();
                        pev?.DevOpenAvatarFlyout();
                        break;
                    case "editsave":
                        if (main.Dialog is ProfileEditorViewModel psv) _ = psv.SaveCommand.ExecuteAsync(null);
                        break;
                    case "logavatars":
                        Log.Info($"DevScript avatars : {string.Join(", ", Data.SettingsStore.Current.Profiles.Select(x => x.Name + "=" + (x.Avatar ?? "initiale")))} | fichiers=[{string.Join(", ", System.IO.Directory.GetFiles(AppPaths.Avatars).Select(System.IO.Path.GetFileName))}] | éditeur={(main.Dialog as ProfileEditorViewModel)?.Avatar}");
                        break;
                    case "confirm": (main.Dialog as ConfirmDialogViewModel)?.ConfirmCommand.Execute(null); break;
                    case "editdelete":
                        if (main.Dialog is ProfileEditorViewModel pdel) _ = pdel.DeleteCommand.ExecuteAsync(null);
                        break;
                    case "pickerselect":
                        if (main.ProfileOverlay is { } psel) _ = psel.SelectCommand.ExecuteAsync(Data.SettingsStore.Current.Profiles[int.Parse(arg)]);
                        break;
                    case "logpicker":
                        Log.Info($"DevScript écran profils : affiché={main.ProfileOverlay is not null} commande occupée={main.ProfileOverlay?.SelectCommand.IsRunning} dialogue={main.Dialog?.GetType().Name} actif={Library.Instance.Profile?.Name}");
                        break;
                    case "unlockcat":
                        if (main.CurrentPage is SettingsViewModel sun && sun.LockedCategories.FirstOrDefault() is { } lockedCat)
                            _ = sun.UnlockCommand.ExecuteAsync(lockedCat);
                        Log.Info($"DevScript déverrouillage demandé : dialogue={main.Dialog?.GetType().Name}");
                        break;
                    case "closewin":
                        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close();
                        await Task.Delay(300);
                        Log.Info($"DevScript fermeture demandée : dialogue={main.Dialog?.GetType().Name}");
                        break;
                    case "pickeradd": if (main.ProfileOverlay is { } po) _ = po.AddCommand.ExecuteAsync(null); break;
                    case "pickeredit": if (main.ProfileOverlay is { } pe) _ = pe.SelectCommand.ExecuteAsync(Data.SettingsStore.Current.Profiles[int.Parse(arg)]); break;
                    case "removepin": if (main.CurrentPage is SettingsViewModel srp) _ = srp.RemovePinCommand.ExecuteAsync(null); break;
                    case "autolock": if (main.CurrentPage is SettingsViewModel sal) sal.AutoLockAdult = arg == "1"; break;
                    case "logkid":
                        Log.Info($"DevScript enfant : actif={Library.Instance.Profile?.Name} kid={Library.Instance.IsKidProfile} déverrouillé={Library.Instance.ParentalUnlocked} code={Data.SettingsStore.Current.HasPin} "
                            + $"autolock={Data.SettingsStore.Current.AutoLockAdult} dialogue={main.Dialog?.GetType().Name} erreur={(main.Dialog as ProfileEditorViewModel)?.Error} "
                            + $"profils=[{string.Join(", ", Data.SettingsStore.Current.Profiles.Select(x => x.Name + (x.IsKid ? "(enfant)" : "")))}]");
                        break;
                    // ── Menu du clic droit et informations sur la lecture ──
                    case "ctxmenu":
                        var wc = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        main.Player.RefreshAudioDevices();
                        var menu = Views.PlayerContextMenu.Build(main.Player);
                        void Dump(System.Collections.IEnumerable items, string indent)
                        {
                            foreach (var it in items)
                            {
                                if (it is Avalonia.Controls.MenuItem mi)
                                {
                                    var head = mi.Header as string ?? string.Join(" | ", (mi.Header as Avalonia.Controls.Panel)?.Children.OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text) ?? Array.Empty<string?>());
                                    Log.Info($"DevScript menu {indent}{(mi.IsChecked ? "● " : "")}{head}{(mi.InputGesture is { } g ? "  [" + g + "]" : "")}{(mi.IsEnabled ? "" : " (inactif)")}");
                                    Dump(mi.Items, indent + "    ");
                                }
                                else if (it is Avalonia.Controls.Separator) Log.Info($"DevScript menu {indent}────");
                            }
                        }
                        Dump(menu.Items, "");
                        if (arg == "show" && wc is not null)
                        {
                            if (main.Player.IsMini)
                            {
                                var mo = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wc).OfType<Views.MiniPlayerOverlay>().FirstOrDefault();
                                if (mo is not null) Views.PlayerContextMenu.Show(main.Player, mo, atPointer: false);
                            }
                            else Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wc).OfType<Views.PlayerView>().FirstOrDefault()?.DevOpenContextMenu();
                        }
                        break;
                    case "ctxsub":
                        // Ouvre le sous-menu dont le titre commence par arg (capture d'écran).
                        var sub = Views.PlayerContextMenu.Last?.Items.OfType<Avalonia.Controls.MenuItem>().FirstOrDefault(m => (m.Header as string)?.StartsWith(arg, StringComparison.OrdinalIgnoreCase) == true);
                        if (sub is not null) sub.IsSubMenuOpen = true;
                        break;
                    case "topmost":
                    case "pos":
                        // Fenêtre de test visible (au premier plan, sans prendre le focus) / position à l'écran.
                        var wt = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (wt is null) break;
                        if (cmd == "topmost") wt.Topmost = arg == "1";
                        else
                        {
                            var pxy = arg.Split(",").Select(int.Parse).ToArray();
                            wt.Position = new Avalonia.PixelPoint(pxy[0], pxy[1]);
                        }
                        break;
                    case "playfile": main.Player.PlayFile(arg, "Test"); break;
                    case "mpvset":
                        var kv = arg.Split(',', 2);
                        main.Player.Engine.SetString(kv[0], kv[1]);
                        break;
                    // ── Déjà vu / filtre des listes ──
                    case "watchfilter":
                        if (main.CurrentPage is VodBrowserViewModel vbw) vbw.SelectedWatchFilter = vbw.WatchFilterOptions.First(o => o.Key == arg);
                        break;
                    case "markwatched":
                    case "markunwatched":
                        if (main.CurrentPage is VodBrowserViewModel vbm && vbm.Items.Count > int.Parse(arg))
                        {
                            var target = vbm.Items[int.Parse(arg)];
                            if (cmd == "markwatched") vbm.MarkWatchedCommand.Execute(target); else vbm.MarkUnwatchedCommand.Execute(target);
                        }
                        break;
                    case "autoplaystart": Library.Instance.Prefs.AutoPlayCountdownStart = arg; Library.Instance.Prefs.AutoPlayCountdown = 8; break;
                    case "cancelnext": main.Player.CancelNextEpisodeCommand.Execute(null); break;
                    case "lognext":
                        Log.Info($"DevScript suivant : épisode={main.Player.CurrentEpisode?.Code} suivant={main.Player.NextEpisode?.Code} décompte={main.Player.NextEpisodeCountdown} texte={main.Player.NextEpisodeText} position={main.Player.Engine.TimePos:0}/{main.Player.Engine.Duration:0} ouvert={main.Player.IsOpen}");
                        break;
                    case "markseason": if (main.CurrentPage is SeriesDetailViewModel msd) msd.ToggleSeasonWatchedCommand.Execute(null); break;
                    case "markseries": if (main.CurrentPage is SeriesDetailViewModel msr) msr.ToggleSeriesWatchedCommand.Execute(null); break;
                    case "season": if (main.CurrentPage is SeriesDetailViewModel sse) sse.SelectedSeason = sse.Seasons[int.Parse(arg)]; break;
                    case "logseries":
                        if (main.CurrentPage is SeriesDetailViewModel lsd)
                            Log.Info($"DevScript série : {lsd.Series.Name} saisonVue={lsd.SeasonWatched} sérieVue={lsd.SeriesWatched} bouton={lsd.PlayLabel} " +
                                     string.Join(" ", lsd.Seasons.Select(x => $"S{x.Number}:{x.Episodes.Count(e => e.IsWatched)}/{x.Episodes.Count}")));
                        break;
                    case "logshortcut":
                        Log.Info($"DevScript raccourci : base={AppContext.BaseDirectory} exe={Environment.ProcessPath} installée={Core.DesktopShortcut.IsAvailable} nôtre={Core.DesktopShortcut.Exists} présent={Core.DesktopShortcut.AnyExists} proposé={Data.SettingsStore.Current.DesktopShortcutAsked} premier={Core.DesktopShortcut.FirstRunAfterInstall} dialogue={main.Dialog?.GetType().Name}");
                        break;
                    case "shortcutset": if (main.CurrentPage is SettingsViewModel ssc) ssc.DesktopShortcutOn = arg == "1"; break;
                    case "logvod":
                        if (main.CurrentPage is VodBrowserViewModel vbl)
                            Log.Info($"DevScript liste : {vbl.Title} filtre={vbl.SelectedWatchFilter.Key} éléments={vbl.Items.Count} vus={vbl.Items.Count(x => x is Movie mv ? mv.IsWatched : x.IsInProgress)} premiers=[{string.Join(", ", vbl.Items.Take(4).Select(x => x.Name + (x.IsWatched ? " ✓" : x.IsInProgress ? " …" : "")))}]");
                        break;
                    case "logfps":
                        // Cadence réelle des images de l'interface (rappels d'animation pendant 2 s).
                        var wf = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (wf is not null)
                        {
                            var ticks = new List<TimeSpan>();
                            var swf = System.Diagnostics.Stopwatch.StartNew();
                            void OnFrame(TimeSpan ts) { ticks.Add(ts); if (swf.ElapsedMilliseconds < 2000) wf.RequestAnimationFrame(OnFrame); }
                            wf.RequestAnimationFrame(OnFrame);
                            await Task.Delay(2300);
                            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
                            Log.Info($"DevScript images interface : {ticks.Count} en 2 s, écart moyen={(gaps.Count > 0 ? gaps.Average() : 0):0.0} ms, max={(gaps.Count > 0 ? gaps.Max() : 0):0.0} ms");
                        }
                        break;
                    case "mpvlog": Player.MpvApi.mpv_request_log_messages(main.Player.Engine.Handle, Player.MpvApi.Utf8(arg)); break;
                    case "logsync":
                        // Fluidité : cadence de l'écran, images perdues / en retard (mesures de mpv).
                        var eng = main.Player.Engine;
                        Log.Info($"DevScript synchro : pos={eng.GetDouble("time-pos"):0.0} écran={eng.GetString("display-fps")} vidéo={eng.GetString("estimated-vf-fps")} "
                            + $"perdues(vo)={eng.GetString("frame-drop-count")} perdues(décodeur)={eng.GetString("decoder-frame-drop-count")} "
                            + $"en retard={eng.GetString("vo-delayed-frame-count")} mal calées={eng.GetString("mistimed-frame-count")} "
                            + $"sync={eng.GetString("video-sync")} hw={eng.GetString("hwdec-current")} gpu-api={eng.GetString("gpu-api")}");
                        Log.Info("DevScript cadence : " + Player.MpvVideoView.Timing.Report());
                        if (arg == "reset") Player.MpvVideoView.Timing.Reset();
                        break;
                    case "stats": main.Player.ToggleStatsCommand.Execute(null); break;
                    case "updatecheck": await Core.Updates.CheckAsync(); break;
                    case "updatechannel": Core.Updates.SetChannel(arg == "beta"); break;
                    case "changelog": main.ShowChangelog(); break;
                    case "logtest": Log.Info("DevScript message : " + arg); break;
                    case "httptest":
                        // Requête avec le client choisi pour cette adresse (strict, sauf source qui accepte un certificat non valide).
                        try
                        {
                            using var hreq = Http.Request(arg);
                            using var hresp = await Http.ForUrl(arg).SendAsync(hreq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                            Log.Info($"DevScript réseau {arg} : HTTP {(int)hresp.StatusCode}");
                        }
                        catch (Exception hex) { Log.Info($"DevScript réseau {arg} : échec, certificat={Http.IsCertificateError(hex)}"); }
                        break;
                    case "allowcert":
                        // allowcert=hôte : marque la source de ce serveur (ou en crée une factice) comme acceptant un certificat non valide.
                        Data.SettingsStore.Current.Sources.Add(new SourceConfig { Name = "Test certificat", Kind = SourceKind.M3u, Url = "https://" + arg + "/x.m3u", AllowInvalidCertificate = true });
                        break;
                    case "logpin": Log.Info($"DevScript code : dialogue={main.Dialog?.GetType().Name} erreur={(main.Dialog as PinDialogViewModel)?.Error ?? (main.Dialog as PasswordDialogViewModel)?.Error} empreinte={Data.SettingsStore.Current.ParentalPinHash?.Split('$')[0]}"); break;
                    case "logbackup":
                        var bsum = (main.Dialog as BackupDialogViewModel)?.Summary ?? "";
                        Log.Info("DevScript sauvegarde : " + bsum.Replace("\n", " | "));
                        break;
                    case "hoverpref": Library.Instance.Prefs.HoverPreview = arg == "1"; break;
                    case "logtip":
                    case "opentip":
                        // État de l'infobulle (fiche de chaîne) des éléments de la liste du lecteur ; opentip=n l'ouvre de force.
                        var tw = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (tw is null) break;
                        var tipItems = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tw).OfType<Avalonia.Controls.ListBoxItem>()
                            .Where(x => x.DataContext is Core.LiveChannel && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(x).OfType<Avalonia.Controls.Control>().Any(a => a.Name == "ZapList")).ToList();
                        if (cmd == "opentip") { Avalonia.Controls.ToolTip.SetIsOpen(tipItems[int.Parse(arg)], true); break; }
                        foreach (var ti in tipItems.Take(4))
                            Log.Info($"DevScript infobulle {((Core.LiveChannel)ti.DataContext!).Name} : fiche={Avalonia.Controls.ToolTip.GetTip(ti)?.GetType().Name} ouverte={Avalonia.Controls.ToolTip.GetIsOpen(ti)} transparence={(Avalonia.Controls.ToolTip.GetTip(ti) is Avalonia.Controls.Control tc ? Avalonia.Controls.TopLevel.GetTopLevel(tc)?.ActualTransparencyLevel.ToString() : null)} délai={Avalonia.Controls.ToolTip.GetShowDelay(ti)} survol={ti.IsPointerOver} actif={tw.IsActive}");
                        break;
                    case "channelui":
                        // Comme le choix dans Paramètres (confirmation, puis sauvegarde automatique avant la bêta).
                        if (main.CurrentPage is SettingsViewModel chs) chs.UpdateChannelIndex = int.Parse(arg);
                        break;
                    case "backup":
                        // backup=chemin[|mot de passe]
                        var bka = arg.Split('|');
                        var bkm = await Core.Backup.CreateAsync(bka[0], bka.Length > 1 ? bka[1] : null);
                        Log.Info($"DevScript sauvegarde : secrets={bkm.Secrets} profils={bkm.Profiles} sources={bkm.Sources} avatars={bkm.Avatars} historique={bkm.History} maliste={bkm.MyList} favoris={bkm.Favorites} enregistrements={bkm.Recordings}");
                        break;
                    case "restore":
                        // restore=chemin[|mot de passe] : prépare puis redémarre, comme le dialogue.
                        var rsa = arg.Split('|');
                        await Core.Backup.CreateAutomaticAsync("avant-restauration");
                        Core.Backup.PrepareRestore(rsa[0], rsa.Length > 1 ? rsa[1] : null);
                        main.RestartForRestore();
                        break;
                    case "backupdialog":
                        main.ShowDialog(new BackupDialogViewModel(main, arg == "restore" ? BackupDialogMode.Restore : BackupDialogMode.Create));
                        break;
                    case "backupload": if (main.Dialog is BackupDialogViewModel bdv) await bdv.LoadAsync(arg); break;
                    case "logdata":
                        var ds = Data.SettingsStore.Current;
                        var dcount = Data.UserDataStore.CountUserData();
                        Log.Info($"DevScript données : profils=[{string.Join(", ", ds.Profiles.Select(x => x.Name + (x.HasPassword ? "(mdp)" : "") + (x.Avatar is null ? "" : "(avatar)")))}] "
                            + $"sources=[{string.Join(", ", ds.Sources.Select(x => x.Name + ":" + (string.IsNullOrEmpty(x.PasswordProtected) ? "sans mdp" : x.Password is null ? "mdp illisible" : "mdp ok")))}] "
                            + $"tmdb={(string.IsNullOrEmpty(ds.TmdbKeyProtected) ? "aucune" : ds.TmdbKey is null ? "illisible" : "ok")} code={(ds.HasPin ? "oui" : "non")} canal={ds.ReleaseChannel ?? "auto"}{(Core.Updates.IsBeta ? "(bêta)" : "(stable)")} "
                            + $"avatars={System.IO.Directory.GetFiles(AppPaths.Avatars).Length} " + string.Join(" ", dcount.Select(kv => kv.Key + "=" + kv.Value)));
                        break;
                    case "logupdate":
                        Log.Info($"DevScript mise à jour : source={Core.Updates.Feed} installée={Core.Updates.IsAvailable} état={Core.Updates.State} version={Core.Updates.CurrentVersion} prête={Core.Updates.ReadyVersion} erreur={Core.Updates.Error}");
                        break;
                    case "restartupdate": main.RestartForUpdate(); break;
                    case "logstats":
                        Log.Info($"DevScript entête : {main.Player.VideoInfo}");
                        foreach (var r in main.Player.Stats) Log.Info($"DevScript stats {r.Label} = {r.Value}");
                        break;
                    case "speed": main.Player.SetSpeedCommand.Execute(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)); break;
                    case "aspect": main.Player.SetAspectCommand.Execute(arg); break;
                    case "minihud":
                        // Affiche les commandes du mini-lecteur sans survol (capture d'écran).
                        var whud = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var hud = whud is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(whud).OfType<Avalonia.Controls.Panel>().FirstOrDefault(p => p.Name == "Hud");
                        if (hud is not null) { hud.Transitions = null; hud.Opacity = 1; }
                        break;
                    case "miniback": Data.SettingsStore.Current.MiniPlayerOnBack = arg == "1"; break;
                    case "minisize":
                        Data.SettingsStore.Current.MiniPlayerSize = int.Parse(arg);
                        Data.SettingsStore.Current.MiniPlayerWidth = Data.AppSettings.MiniPresetWidths[int.Parse(arg)];
                        main.Player.NotifyMiniLayoutChanged();
                        break;
                    case "minidrag":
                    case "miniclick":
                    case "miniresize":
                        // Glisser (dx,dy), redimensionner (bord,dx,dy : l r t b tl tr bl br) ou clic sur le mini-lecteur, par événements simulés.
                        var wm = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as Views.MainWindow;
                        var parts2 = arg.Split(',');
                        var gripArg = cmd == "miniresize" ? parts2[0] : null;
                        var nums = cmd == "miniclick" ? new[] { 0.0, 0.0 } : parts2.Skip(cmd == "miniresize" ? 1 : 0).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        if (wm is not null) await wm.DevMiniGestureAsync(nums[0], nums[1], gripArg);
                        await Task.Delay(400);
                        break;
                    case "logmini":
                        var wmi = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var host = wmi is null ? null : Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Border>(wmi, "PlayerHost");
                        var vslider = wmi is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wmi).OfType<Avalonia.Controls.Slider>().FirstOrDefault(sl => sl.Name == "VolumeSlider");
                        Log.Info($"DevScript mini : volume={main.Player.Volume:0} muet={main.Player.IsMuted} curseur={(vslider is null ? null : Avalonia.VisualExtensions.TranslatePoint(vslider, default, wmi!))}/{vslider?.Bounds.Size} ouvert={main.Player.IsOpen} mini={main.Player.IsMini} appli={main.Player.ShowsApp} pause={main.Player.IsPaused} page={main.CurrentPage?.GetType().Name} largeur={Data.SettingsStore.Current.MiniPlayerWidth} x={Data.SettingsStore.Current.MiniPlayerX} y={Data.SettingsStore.Current.MiniPlayerY} cadre={host?.Bounds} plein={main.IsFullscreen}");
                        break;
                    case "gesture":
                        // Rejoue un geste souris sur l'image du lecteur puis journalise l'état de lecture.
                        var wg = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var pv = wg is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wg).OfType<Views.PlayerView>().FirstOrDefault();
                        var before = main.Player.IsPaused;
                        if (pv is not null) await pv.DevGestureAsync(arg);
                        await Task.Delay(700);
                        Log.Info($"DevScript geste={arg} vue={(pv is null ? "absente" : "ok")} pause avant={before} après={main.Player.IsPaused} plein={main.IsFullscreen} panneau={main.Player.IsSidePanelOpen}");
                        break;
                    case "logwin":
                        var wl = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (wl is not null) Log.Info($"DevScript fenêtre état={wl.WindowState} pos={wl.Position} taille={wl.Width:0}x{wl.Height:0} plein={main.IsFullscreen}");
                        break;
                    case "preview":
                        var wp = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        var card = wp is null ? null : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(wp).OfType<Views.PosterCard>()
                            .Where(x => x.IsEffectivelyVisible && x.DataContext is Core.Movie or Core.Series).Skip(int.Parse(arg)).FirstOrDefault();
                        if (card is not null) Views.PosterPreviewController.Current?.DevOpen(card);
                        break;
                    case "previewvol":
                        if (Views.PosterPreviewController.Current?.DevVm is { } pvm) pvm.Volume = int.Parse(arg);
                        break;
                    case "previewmute":
                        if (Views.PosterPreviewController.Current?.DevVm is { } pvm2) pvm2.IsMuted = arg == "1";
                        break;
                    case "previewreport": Views.PosterPreviewController.Current?.DevReportVolume(); break;
                    case "trailer": await main.OpenTrailerAsync(c.Movies[int.Parse(arg)]); break;
                    case "dtrailer": if (main.CurrentPage is DetailViewModelBase dvm) await dvm.OpenTrailerCommand.ExecuteAsync(null); break;
                    case "drag":
                        // drag=x1,y1,x2,y2 : bouton gauche enfoncé en (x1,y1), glissé en 12 pas, relâché en (x2,y2).
                        var dv = arg.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        RawMouse(Avalonia.Input.Raw.RawPointerEventType.Move, dv[0], dv[1], Avalonia.Input.RawInputModifiers.None);
                        RawMouse(Avalonia.Input.Raw.RawPointerEventType.LeftButtonDown, dv[0], dv[1], Avalonia.Input.RawInputModifiers.LeftMouseButton);
                        for (var k = 1; k <= 12; k++)
                        {
                            await Task.Delay(12);
                            RawMouse(Avalonia.Input.Raw.RawPointerEventType.Move, dv[0] + (dv[2] - dv[0]) * k / 12, dv[1] + (dv[3] - dv[1]) * k / 12,
                                Avalonia.Input.RawInputModifiers.LeftMouseButton);
                        }
                        RawMouse(Avalonia.Input.Raw.RawPointerEventType.LeftButtonUp, dv[2], dv[3], Avalonia.Input.RawInputModifiers.None);
                        Log.Info($"DevScript glisser {arg} : page={main.CurrentPage?.GetType().Name}");
                        break;
                    case "wheel":
                        // wheel=x,y,dy : un cran de molette (dy > 0 vers le haut) à la position donnée.
                        var wv = arg.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        RawMouse(Avalonia.Input.Raw.RawPointerEventType.Move, wv[0], wv[1], Avalonia.Input.RawInputModifiers.None);
                        RawWheel(wv[0], wv[1], wv[2]);
                        break;
                    case "lograils":
                        var rw = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (rw is null) break;
                        foreach (var rail in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rw).OfType<Controls.Rail>().Where(r => r.IsEffectivelyVisible).Take(int.TryParse(arg, out var rn) ? rn : 4))
                        {
                            var rsv = rail.Children.OfType<Avalonia.Controls.ScrollViewer>().First();
                            var arrows = rail.Children.OfType<Avalonia.Controls.Button>().Select(b => (b.IsHitTestVisible ? "visible" : "cachée") + $"({b.Opacity:0.##}@{b.Bounds})");
                            var bar = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(rsv).OfType<Avalonia.Controls.Primitives.ScrollBar>().FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
                            Log.Info($"DevScript rangée à {Avalonia.VisualExtensions.TranslatePoint(rail, default, rw)} taille={rail.Bounds.Size} décalage={rsv.Offset.X:0} sur {Math.Max(0, rsv.Extent.Width - rsv.Viewport.Width):0} survol={rail.IsPointerOver} flèches={string.Join("/", arrows)} ascenseur={bar?.Bounds.Height:0.#}px déployé={bar?.IsExpanded}");
                        }
                        break;
                    case "quit":
                        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("DevScript " + raw, ex);
            }
        }
    }

    private static void RawWheel(double x, double y, double dy)
    {
        var win = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (win?.PlatformImpl is not { } impl) return;
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var t = impl.GetType();
        var inputProp = t.GetProperty("Input", F) ?? t.GetInterfaces().Select(i => i.GetProperty("Input", F)).FirstOrDefault(p => p is not null);
        if (inputProp?.GetValue(impl) is not Action<Avalonia.Input.Raw.RawInputEventArgs> input) return;
        object? device = null;
        for (var tt = t; tt is not null && device is null; tt = tt.BaseType)
            device = tt.GetFields(F).Where(fl => typeof(Avalonia.Input.IPointerDevice).IsAssignableFrom(fl.FieldType)).Select(fl => fl.GetValue(impl)).FirstOrDefault(v => v is Avalonia.Input.MouseDevice);
        if (device is null) return;
        var ctor = typeof(Avalonia.Input.Raw.RawMouseWheelEventArgs).GetConstructors(F).First(c => c.GetParameters().Length == 6);
        input((Avalonia.Input.Raw.RawInputEventArgs)ctor.Invoke(new object[] { device, (ulong)Environment.TickCount64, win, new Avalonia.Point(x, y), new Avalonia.Vector(0, dy), Avalonia.Input.RawInputModifiers.None }));
    }

    /// <summary>Événement souris brut injecté dans la fenêtre (le vrai curseur ne bouge pas) : tests uniquement.</summary>
    private static void RawMouse(Avalonia.Input.Raw.RawPointerEventType type, double x, double y, Avalonia.Input.RawInputModifiers mods)
    {
        var win = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (win?.PlatformImpl is not { } impl) return;
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var t = impl.GetType();
        var inputProp = t.GetProperty("Input", F) ?? t.GetInterfaces().Select(i => i.GetProperty("Input", F)).FirstOrDefault(p => p is not null);
        if (inputProp?.GetValue(impl) is not Action<Avalonia.Input.Raw.RawInputEventArgs> input) return;
        object? device = null;
        for (var tt = t; tt is not null && device is null; tt = tt.BaseType)
            device = tt.GetFields(F).Where(fl => typeof(Avalonia.Input.IPointerDevice).IsAssignableFrom(fl.FieldType)).Select(fl => fl.GetValue(impl)).FirstOrDefault(v => v is Avalonia.Input.MouseDevice);
        if (device is null) return;
        var ctor = typeof(Avalonia.Input.Raw.RawPointerEventArgs).GetConstructors(F)
            .First(c => c.GetParameters().Length == 6 && c.GetParameters()[4].ParameterType == typeof(Avalonia.Point));
        input((Avalonia.Input.Raw.RawInputEventArgs)ctor.Invoke(new object[] { device, (ulong)Environment.TickCount64, win, type, new Avalonia.Point(x, y), mods }));
    }
}
