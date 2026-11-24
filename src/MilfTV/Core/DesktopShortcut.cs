using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MilfTV.Core;

/// <summary>Raccourci MilfTV sur le bureau, pour la copie installée seulement. L'installateur (Velopack) ne crée que
/// celui du menu Démarrer ; MilfTV propose le raccourci du bureau au premier lancement, et Paramètres permet de
/// l'ajouter ou de le retirer. La désinstallation le retire (crochet Velopack).</summary>
public static class DesktopShortcut
{
    private const string FileName = "MilfTV.lnk";

    /// <summary>Premier lancement juste après l'installation (crochet Velopack) : la question est reposée même si
    /// une installation précédente l'avait déjà posée (réglages gardés après une désinstallation).</summary>
    public static bool FirstRunAfterInstall { get; set; }

    /// <summary>Dossier de l'exécutable. Pas AppContext.BaseDirectory : en fichier unique, il peut désigner le dossier
    /// d'extraction temporaire.</summary>
    private static string AppDir => Path.TrimEndingDirectorySeparator(
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory);

    /// <summary>Dossier d'installation Velopack (contient Update.exe et le dossier « current »).</summary>
    private static string? InstallRoot
    {
        get
        {
            if (!Path.GetFileName(AppDir).Equals("current", StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetDirectoryName(AppDir);
            return root is not null && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
        }
    }

    /// <summary>Copie installée (pas la version portable ni le développement).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() && InstallRoot is not null;

    private static string DesktopDir =>
        ViewModels.DevScript.ToolsCompiled && Environment.GetEnvironmentVariable("MILFTV_DESKTOP") is { Length: > 0 } test
            ? test // tests : un dossier à part, jamais le vrai bureau
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string LinkPath => Path.Combine(DesktopDir, FileName);

    /// <summary>Un raccourci « MilfTV » est déjà sur le bureau (le nôtre ou un autre).</summary>
    public static bool AnyExists => File.Exists(LinkPath);

    /// <summary>Le raccourci du bureau mène à cette copie installée.</summary>
    public static bool Exists => InstallRoot is { } root && File.Exists(LinkPath) && PointsInto(LinkPath, root);

    public static bool Create()
    {
        if (!IsAvailable || Environment.ProcessPath is not { } exe) return false;
        try
        {
            Directory.CreateDirectory(DesktopDir);
            var link = (IShellLinkW)new CShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            link.SetDescription("MilfTV");
            // Même identifiant que le processus (fixé par Velopack) : la fenêtre se range sous ce raccourci
            // dans la barre des tâches, comme avec celui du menu Démarrer.
            if (GetCurrentProcessExplicitAppUserModelID(out var aumidPtr) == 0 && aumidPtr != IntPtr.Zero)
            {
                var aumid = Marshal.PtrToStringUni(aumidPtr);
                Marshal.FreeCoTaskMem(aumidPtr);
                if (!string.IsNullOrEmpty(aumid)) SetAppUserModelId(link, aumid);
            }
            ((IPersistFile)link).Save(LinkPath, true);
            Log.Info("Raccourci ajouté sur le bureau : " + LinkPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Raccourci du bureau non créé : " + ex.Message);
            return false;
        }
    }

    /// <summary>Retire le raccourci du bureau s'il mène à cette copie installée (jamais celui d'une autre copie).</summary>
    public static bool Remove()
    {
        try
        {
            if (!Exists) return !AnyExists;
            File.Delete(LinkPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Raccourci du bureau non retiré : " + ex.Message);
            return false;
        }
    }

    /// <summary>Crochet de désinstallation : rapide, sans journal (l'application n'est pas initialisée).</summary>
    public static void RemoveOnUninstall()
    {
        try
        {
            if (InstallRoot is { } root && File.Exists(LinkPath) && PointsInto(LinkPath, root)) File.Delete(LinkPath);
        }
        catch { /* la désinstallation continue */ }
    }

    private static bool PointsInto(string lnk, string root)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            // Noms longs des deux côtés : un même dossier peut s'écrire « C:\PROGRA~1\… » pour « C:\Program Files\… » (nom court 8.3).
            return target.Length > 0 && LongPath(Path.GetFullPath(target)).StartsWith(
                Path.TrimEndingDirectorySeparator(LongPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ───────────── COM (IShellLink, IPropertyStore) ─────────────

    private static void SetAppUserModelId(IShellLinkW link, string aumid)
    {
        var store = (IPropertyStore)link;
        var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
        var value = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(aumid) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer);
        }
    }

    private static string LongPath(string path)
    {
        var sb = new StringBuilder(1024);
        var n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
