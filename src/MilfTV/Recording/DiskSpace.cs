using System.Runtime.InteropServices;
using MilfTV.Data;
using MilfTV.Localization;

namespace MilfTV.Recording;

/// <summary>Espace libre du disque qui reçoit les enregistrements (seuil réglé dans RecordingMinFreeGb).</summary>
public static class DiskSpace
{
    private const double GiB = 1024d * 1024 * 1024;

    /// <summary>Intervalle minimal entre deux contrôles pendant un enregistrement.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    /// <summary>Octets disponibles pour l'utilisateur (quotas compris) ; null si inconnu.
    /// GetDiskFreeSpaceEx gère aussi les partages réseau (\\serveur\partage) et les dossiers montés, que DriveInfo ne voit pas.</summary>
    public static long? FreeBytes(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var full = Path.GetFullPath(folder);
            // Le dossier peut ne pas exister encore : on remonte jusqu'au premier parent existant.
            var dir = full;
            while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is { } parent) dir = parent;
            if (GetDiskFreeSpaceEx(dir.EndsWith('\\') ? dir : dir + "\\", out var free, out _, out _)) return (long)free;
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
                return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { /* disque inaccessible : pas de contrôle */ }
        return null;
    }

    /// <summary>Vrai si l'espace libre est connu et inférieur au seuil réglé (0 Go = contrôle désactivé).</summary>
    public static bool IsLow(string? folder, out double freeGb)
    {
        freeGb = 0;
        var minGb = SettingsStore.Current.RecordingMinFreeGb;
        if (minGb <= 0) return false;
        if (FreeBytes(folder) is not { } free) return false;
        freeGb = free / GiB;
        return free < minGb * GiB;
    }

    /// <summary>Message d'erreur : espace libre restant et seuil réglé.</summary>
    public static string LowMessage(double freeGb) => Loc.T("Rec.LowDisk", freeGb, SettingsStore.Current.RecordingMinFreeGb);
}
