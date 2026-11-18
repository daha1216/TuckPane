using System.Security.Cryptography;
using System.Text.Json;

namespace TuckPane.Updates;

public static class UpdateFiles
{
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Hash(string path)
    {
        using Stream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain filesystem links.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static string BackupConfiguration(string statePath, string backupRoot, string version)
    {
        string destination = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + version + "-" + Guid.NewGuid().ToString("N")[..8]);
        NoLinks(destination);
        NoLinks(statePath);
        Directory.CreateDirectory(destination);
        // The saved state contains window positions and all storage associations.
        // Document bodies and actual organizer files remain in their original directories.
        bool statePresent = File.Exists(statePath);
        if (statePresent)
        {
            string copiedState = Path.Combine(destination, "state.json");
            File.Copy(statePath, copiedState);
            if (Hash(statePath) != Hash(copiedState)) throw new IOException("Configuration backup verification failed.");
        }
        if (File.Exists(statePath + ".bak")) File.Copy(statePath + ".bak", Path.Combine(destination, "state.json.bak"));
        WriteJson(Path.Combine(destination, "backup.json"), new { version, source = statePath, statePresent, createdUtc = DateTimeOffset.UtcNow });
        return destination;
    }

    public const int BackupRetentionCount = 10;

    /// <summary>
    /// Keeps at most <paramref name="keep"/> newest valid configuration backups within
    /// <paramref name="maxAge"/>. Only directories carrying a <c>backup.json</c> marker
    /// that are not filesystem links are pruned, and <paramref name="current"/> is always retained.
    /// </summary>
    public static int PruneBackups(string backupRoot, string? current, int keep, TimeSpan? maxAge = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(backupRoot)) return 0;
            string? protectedFull = string.IsNullOrWhiteSpace(current)
                ? null
                : Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo[] backups = new DirectoryInfo(backupRoot)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    File.Exists(Path.Combine(directory.FullName, "backup.json")) &&
                    (protectedFull is null || !Path.GetFullPath(directory.FullName)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Equals(protectedFull, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(directory => directory.CreationTimeUtc)
                .ThenByDescending(directory => directory.Name, StringComparer.Ordinal)
                .ToArray();
            DateTime cutoff = maxAge is { } age ? DateTime.UtcNow - age : DateTime.MinValue;
            int retainCount = Math.Max(1, keep);
            for (int index = 0; index < backups.Length; index++)
            {
                DirectoryInfo candidate = backups[index];
                if (index < retainCount && candidate.CreationTimeUtc >= cutoff) continue;
                try
                {
                    NoLinks(candidate.FullName);
                    DeleteBackupDirectory(candidate.FullName);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static void DeleteBackupDirectory(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(entry)) DeleteBackupDirectory(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory, recursive: false);
    }
}

public sealed record UpdateCheckCache(DateTimeOffset LastAttemptUtc, string? ReleaseJson);
public sealed record UpdateRequest(int ParentId, long ParentStartTicks, string TargetDirectory, string PackagePath,
    string PackageHash, string Version, bool Installed, string ResultPath, string[] ProtectedPaths, string? TestRoot);
public sealed record UpdateResult(string Status, string Version, string Message, string BackupDirectory);


