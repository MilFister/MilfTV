using CommunityToolkit.Mvvm.ComponentModel;
using MilfTV.Localization;
using CommunityToolkit.Mvvm.Input;
using MilfTV.Core;
using MilfTV.Data;
using MilfTV.Providers;

namespace MilfTV.ViewModels;

public sealed partial class ConfirmDialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public ConfirmDialogViewModel(string title, string message, string confirmText)
    {
        DialogTitle = title;
        Message = message;
        ConfirmText = confirmText;
    }

    public string DialogTitle { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public Task<bool> Task => _tcs.Task;
    public event Action<bool>? Completed;

    public void Complete(bool result)
    {
        if (_tcs.TrySetResult(result)) Completed?.Invoke(result);
    }

    [RelayCommand] private void Confirm() => Complete(true);
    [RelayCommand] private void Cancel() => Complete(false);
}

public enum PinDialogMode { Verify, Create }

public sealed partial class PinDialogViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Action<bool> _done;
    private string? _firstEntry;

    public PinDialogViewModel(MainViewModel main, PinDialogMode mode, Action<bool> done, string? prompt = null)
    {
        _main = main;
        Mode = mode;
        _done = done;
        _firstPrompt = prompt ?? (mode == PinDialogMode.Verify ? Loc.T("Pin.Enter") : Loc.T("Pin.Choose"));
        _prompt = _firstPrompt;
    }

    private readonly string _firstPrompt;

    public PinDialogMode Mode { get; }
    [ObservableProperty] private string _prompt;
    [ObservableProperty] private string _pin = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private int _shake;

    public string Dots => new string('●', Pin.Length) + new string('○', 4 - Pin.Length);
    public string[] Digits { get; } = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    partial void OnPinChanged(string value) => OnPropertyChanged(nameof(Dots));

    [RelayCommand]
    public void Digit(string d)
    {
        if (Pin.Length >= 4) return;
        Error = null;
        Pin += d;
        if (Pin.Length == 4) Submit();
    }

    [RelayCommand]
    public void Backspace()
    {
        if (Pin.Length > 0) Pin = Pin[..^1];
    }

    private void Submit()
    {
        if (Mode == PinDialogMode.Verify)
        {
            if (Library.Instance.CheckPin(Pin)) _done(true);
            else
            {
                Error = Loc.T("Pin.Wrong");
                Pin = "";
                Shake++;
            }
            return;
        }
        if (_firstEntry is null)
        {
            _firstEntry = Pin;
            Pin = "";
            Prompt = Loc.T("Pin.Confirm");
            return;
        }
        if (_firstEntry == Pin)
        {
            SettingsStore.Current.ParentalPinHash = Secret.HashPin(Pin);
            SettingsStore.Save();
            Library.Instance.ParentalUnlocked = false;
            Library.Instance.RefreshParental();
            _done(true);
        }
        else
        {
            Error = Loc.T("Pin.Mismatch");
            _firstEntry = null;
            Pin = "";
            Prompt = _firstPrompt;
            Shake++;
        }
    }

    [RelayCommand] private void Cancel() => _done(false);
}

/// <summary>Ajout / modification d'une source IPTV.</summary>
public sealed partial class SourceEditorViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly SourceConfig? _original;

    public SourceEditorViewModel(MainViewModel main, SourceConfig? existing, SourceKind kind)
    {
        _main = main;
        _original = existing;
        IsNew = existing is null;
        if (existing is not null)
        {
            Name = existing.Name;
            Url = existing.Url;
            Username = existing.Username ?? "";
            Password = existing.Password ?? "";
            Mac = existing.MacAddress ?? "00:1A:79:";
            EpgUrl = existing.EpgUrl ?? "";
            UserAgent = existing.UserAgent ?? "";
            IsShared = existing.IsShared;
        }
        SelectedKind = kind;
    }

    public bool IsNew { get; }
    public string DialogTitle => IsNew ? Loc.T("Source.AddTitle") : Loc.T("Source.EditTitle");

    [ObservableProperty] private SourceKind _selectedKind;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _mac = "00:1A:79:";
    [ObservableProperty] private string _epgUrl = "";
    [ObservableProperty] private string _userAgent = "";
    [ObservableProperty] private bool _showAdvanced;
    /// <summary>Visible par les autres profils (qui ne pourront pas la modifier).</summary>
    [ObservableProperty] private bool _isShared;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusOk;
    [ObservableProperty] private bool _xtreamLinkDetected;

    public bool IsXtream => SelectedKind == SourceKind.Xtream;
    public bool IsM3u => SelectedKind == SourceKind.M3u;
    public bool IsStalker => SelectedKind == SourceKind.Stalker;
    public string UrlLabel => SelectedKind switch
    {
        SourceKind.Xtream => Loc.T("Source.ServerAddress"),
        SourceKind.M3u => Loc.T("Source.PlaylistLink"),
        _ => Loc.T("Source.PortalAddress")
    };
    public string UrlWatermark => SelectedKind switch
    {
        SourceKind.Xtream => "http://example.com:8080",
        SourceKind.M3u => "http://example.com/playlist.m3u  |  C:\\…\\playlist.m3u",
        _ => "http://example.com:8080/c/"
    };

    partial void OnSelectedKindChanged(SourceKind value)
    {
        OnPropertyChanged(nameof(IsXtream));
        OnPropertyChanged(nameof(IsM3u));
        OnPropertyChanged(nameof(IsStalker));
        OnPropertyChanged(nameof(UrlLabel));
        OnPropertyChanged(nameof(UrlWatermark));
        Status = null;
        CheckXtreamLink();
    }

    partial void OnUrlChanged(string value) => CheckXtreamLink();

    private void CheckXtreamLink() =>
        XtreamLinkDetected = SelectedKind == SourceKind.M3u && XtreamProvider.TryParseM3uLink(Url, out _, out _, out _);

    [RelayCommand]
    private void SetKind(string kind) =>
        SelectedKind = kind switch { "m3u" => SourceKind.M3u, "stalker" => SourceKind.Stalker, _ => SourceKind.Xtream };

    [RelayCommand]
    private void ConvertToXtream()
    {
        if (!XtreamProvider.TryParseM3uLink(Url, out var server, out var user, out var pass)) return;
        SelectedKind = SourceKind.Xtream;
        Url = server;
        Username = user;
        Password = pass;
        Status = Loc.T("Source.Converted");
        StatusOk = true;
    }

    private SourceConfig Build()
    {
        var src = _original?.Clone() ?? new SourceConfig();
        src.Kind = SelectedKind;
        src.Url = Url.Trim();
        src.Username = IsXtream ? Username.Trim() : null;
        src.Password = IsXtream ? Password.Trim() : null;
        src.MacAddress = IsStalker ? Mac.Trim().ToUpperInvariant() : null;
        src.EpgUrl = string.IsNullOrWhiteSpace(EpgUrl) ? null : EpgUrl.Trim();
        src.UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim();
        src.Name = string.IsNullOrWhiteSpace(Name) ? DefaultName(src) : Name.Trim();
        src.IsShared = IsShared;
        return src;
    }

    private static string DefaultName(SourceConfig s)
    {
        if (Uri.TryCreate(s.Url.Contains("://") ? s.Url : "http://" + s.Url, UriKind.Absolute, out var u) && !File.Exists(s.Url))
            return u.Host;
        return Path.GetFileNameWithoutExtension(s.Url) is { Length: > 0 } f ? f : Loc.T("Source.DefaultName");
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Url)) return Loc.T("Source.FieldRequired", UrlLabel);
        if (IsXtream && (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)))
            return Loc.T("Source.CredentialsRequired");
        if (IsStalker && !System.Text.RegularExpressions.Regex.IsMatch(Mac.Trim(), "^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"))
            return Loc.T("Source.MacInvalid");
        return null;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Validate() is { } err) { Status = err; StatusOk = false; return; }
        IsBusy = true;
        Status = Loc.T("Source.Testing");
        StatusOk = true;
        try
        {
            using var provider = ProviderFactory.Create(Build());
            var info = await provider.TestAsync(CancellationToken.None);
            StatusOk = info.Ok;
            Status = Describe(info);
        }
        catch (Exception ex)
        {
            StatusOk = false;
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(AccountInfo info)
    {
        if (!info.Ok) return info.Message ?? Loc.T("Source.Refused");
        var parts = new List<string> { Loc.T("Source.Success") };
        if (info.ExpiresAt is { } exp) parts.Add(Loc.T("Source.ExpiresOn", TextNorm.FrDate(exp)));
        if (info.MaxConnections is { } mc) parts.Add(Loc.T(mc > 1 ? "Source.Connections.Many" : "Source.Connections.One", mc));
        if (info.IsTrial) parts.Add(Loc.T("Source.Trial"));
        if (!string.IsNullOrWhiteSpace(info.Message) && info.ExpiresAt is null) parts.Add(info.Message!);
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Validate() is { } err) { Status = err; StatusOk = false; return; }
        IsBusy = true;
        try
        {
            var src = Build();
            if (IsNew)
            {
                Status = Loc.T("Common.Checking");
                StatusOk = true;
                using var provider = ProviderFactory.Create(src);
                var info = await provider.TestAsync(CancellationToken.None);
                // Fenêtre fermée pendant le test : l'utilisateur a renoncé, on n'enregistre rien.
                if (!ReferenceEquals(_main.Dialog, this)) return;
                if (!info.Ok)
                {
                    StatusOk = false;
                    Status = Loc.T("Source.FixAndRetry", info.Message ?? Loc.T("Source.Refused"));
                    return;
                }
            }
            await _main.SaveSourceAsync(src, IsNew);
        }
        catch (Exception ex)
        {
            StatusOk = false;
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private void Cancel() => _main.CloseDialog();
}
