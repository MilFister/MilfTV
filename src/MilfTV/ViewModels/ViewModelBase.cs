using CommunityToolkit.Mvvm.ComponentModel;

namespace MilfTV.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Titre affiché dans la barre supérieure.</summary>
    public virtual string Title => "";

    /// <summary>Vrai pour conserver la vue (défilement, sélection) entre deux navigations.</summary>
    public virtual bool KeepView => true;

    public virtual void OnNavigatedTo() { }

    /// <summary>Page abandonnée (cache vidé, historique effacé) : elle se désabonne des événements globaux
    /// pour ne plus travailler en arrière-plan ni rester en mémoire.</summary>
    public virtual void Detach() { }
    public virtual void OnNavigatedFrom() { }
}

public static class Icons
{
    public const string Home = "";
    public const string Tv = "";
    public const string Guide = "";
    public const string Movie = "";
    public const string Series = "";
    public const string Heart = "";
    public const string HeartFill = "";
    public const string Star = "";
    public const string StarFill = "";
    public const string Record = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Back = "";
    public const string Close = "";
    public const string FullScreen = "";
    public const string ExitFullScreen = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Subtitles = "";
    public const string Audio = "";
    public const string List = "";
    public const string Lock = "";
    public const string Unlock = "";
    public const string Refresh = "";
    public const string Add = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string Info = "";
    public const string Check = "";
    public const string Moon = "";
    public const string Sun = "";
    public const string Palette = "";
    public const string Folder = "";
    public const string Back10 = "";
    public const string Forward30 = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Clock = "";
    public const string History = "";
    public const string Aspect = "";
    public const string Link = "";
    public const string Server = "";
    public const string File = "";
    public const string Globe = "";
    public const string Shield = "";
    public const string Warning = "";
}
