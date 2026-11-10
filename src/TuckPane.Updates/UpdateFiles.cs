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
        Directory.CreateDirectory(destination);
        // The saved state contains window positions and all storage associations.
        // Document bodies and actual organizer files remain in their original directories.
        // A brand-new profile may not have written state.json yet; that is not a failure.
        bool statePresent = File.Exists(statePath);
        if (statePresent) File.Copy(statePath, Path.Combine(destination, "state.json"));
        if (File.Exists(statePath + ".bak")) File.Copy(statePath + ".bak", Path.Combine(destination, "state.json.bak"));
        WriteJson(Path.Combine(destination, "backup.json"), new { version, source = statePath, statePresent, createdUtc = DateTimeOffset.UtcNow });
        if (statePresent && Hash(statePath) != Hash(Path.Combine(destination, "state.json")))
            throw new IOException("Configuration backup verification failed.");
        PruneBackups(backupRoot, destination, BackupRetentionCount);
        return destination;
    }

    public const int BackupRetentionCount = 10;

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> configuration backups. Only directories that carry
    /// a backup.json marker and are not filesystem links are candidates, and the backup that was
    /// just written is never removed. Failures are ignored: pruning must never fail an update.
    /// </summary>
    public static int PruneBackups(string backupRoot, string? current, int keep)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(backupRoot)) return 0;
            DirectoryInfo[] backups = new DirectoryInfo(backupRoot)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    File.Exists(Path.Combine(directory.FullName, "backup.json")))
                .OrderByDescending(directory => directory.CreationTimeUtc)
                .ThenByDescending(directory => directory.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (DirectoryInfo stale in backups.Skip(Math.Max(1, keep)))
            {
                if (current is not null && Path.GetFullPath(stale.FullName).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    foreach (FileInfo file in stale.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) == 0) file.Delete();
                    }
                    stale.Delete(recursive: false);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}

public sealed record UpdateCheckCache(DateTimeOffset LastAttemptUtc, string? ReleaseJson);
public sealed record UpdateRequest(int ParentId, long ParentStartTicks, string TargetDirectory, string PackagePath,
    string PackageHash, string Version, bool Installed, string ResultPath, string[] ProtectedPaths, string? TestRoot);
public sealed record UpdateResult(string Status, string Version, string Message, string BackupDirectory);


