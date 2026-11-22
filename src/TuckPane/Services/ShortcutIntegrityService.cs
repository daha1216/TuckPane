using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TuckPane.Services;

internal sealed record ShortcutInspection(
    string RawTargetPath,
    string ExpandedTargetPath,
    string RawWorkingDirectory,
    string EffectiveWorkingDirectory,
    string Arguments,
    string IconPath,
    int IconIndex,
    int ShowCommand,
    uint Flags,
    bool IsMsiAdvertised,
    bool RequiresElevation,
    bool IsVirtualShellTarget,
    bool HasRelativePath);

/// <summary>
/// Inspects and repairs Windows Shell shortcuts (.lnk) and Internet shortcuts (.url)
/// so moving them from User/Public Desktop into TuckPane storage never breaks
/// relative paths, working directories, UWP/MSI links, or admin elevation flags.
/// </summary>
internal static class ShortcutIntegrityService
{
    private const int StgmRead = 0x00000000;
    private const int StgmReadWrite = 0x00000002;

    private static readonly HashSet<string> SafeInternetShortcutSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "mailto",
        "steam",
        "com.epicgames.launcher",
        "uplay",
        "battlenet",
        "origin",
        "origin2",
        "xbox",
        "ms-settings",
        "ms-windows-store",
        "vscode",
        "obsidian",
        "discord",
        "tg",
        "weixin"
    };

    internal static ShortcutInspection? InspectLink(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath) ||
            !Path.GetExtension(shortcutPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        object? instance = null;
        try
        {
            instance = new NativeMethods.ShellLink();
            var link = (NativeMethods.IShellLinkW)instance;
            var persist = (IPersistFile)instance;
            persist.Load(shortcutPath, StgmRead);

            uint flags = 0;
            if (instance is NativeMethods.IShellLinkDataList dataList &&
                dataList.GetFlags(out uint readFlags) >= 0)
            {
                flags = readFlags;
            }

            string rawTarget = ReadString((sb, cap) => link.GetPath(sb, cap, IntPtr.Zero, NativeMethods.SLGP_RAWPATH));
            if (string.IsNullOrWhiteSpace(rawTarget))
                rawTarget = ReadString((sb, cap) => link.GetPath(sb, cap, IntPtr.Zero, 0));

            string expandedTarget = ExpandPath(rawTarget);
            string rawWorkingDir = ReadString((sb, cap) => link.GetWorkingDirectory(sb, cap));
            string arguments = ReadString((sb, cap) => link.GetArguments(sb, cap));

            int iconIndex = 0;
            var iconBuffer = new StringBuilder(32768);
            string iconPath = link.GetIconLocation(iconBuffer, iconBuffer.Capacity, out iconIndex) >= 0
                ? iconBuffer.ToString().Trim()
                : string.Empty;

            int showCmd = NativeMethods.SW_SHOWNORMAL;
            if (link.GetShowCmd(out int readShow) >= 0 && readShow > 0)
                showCmd = readShow;

            bool isMsiAdvertised = (flags & NativeMethods.SLDF_HAS_DARWINID) != 0;
            bool requiresElevation = (flags & NativeMethods.SLDF_RUNAS_USER) != 0;
            bool hasRelativePath = (flags & NativeMethods.SLDF_HAS_RELPATH) != 0;

            bool isVirtualShellTarget = isMsiAdvertised ||
                string.IsNullOrWhiteSpace(expandedTarget) ||
                expandedTarget.StartsWith("::", StringComparison.Ordinal) ||
                expandedTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

            string effectiveWorkingDir = ResolveEffectiveWorkingDirectory(
                rawWorkingDir,
                expandedTarget,
                shortcutPath);

            return new ShortcutInspection(
                RawTargetPath: rawTarget,
                ExpandedTargetPath: expandedTarget,
                RawWorkingDirectory: rawWorkingDir,
                EffectiveWorkingDirectory: effectiveWorkingDir,
                Arguments: arguments,
                IconPath: iconPath,
                IconIndex: iconIndex,
                ShowCommand: showCmd,
                Flags: flags,
                IsMsiAdvertised: isMsiAdvertised,
                RequiresElevation: requiresElevation,
                IsVirtualShellTarget: isVirtualShellTarget,
                HasRelativePath: hasRelativePath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取快捷方式元数据失败：{shortcutPath}", ex);
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>
    /// Repairs a relocated standard filesystem .lnk in place so moving it from Desktop
    /// into a deeper TuckPane storage directory never breaks stale RELATIVE_PATH entries
    /// or empty/relative WorkingDirectory values, while leaving MSI/UWP/PIDL links untouched.
    /// </summary>
    internal static bool RepairMovedShortcutIfNeeded(string originalSourcePath, string importedDestinationPath)
    {
        if (string.IsNullOrWhiteSpace(importedDestinationPath) ||
            !File.Exists(importedDestinationPath) ||
            !Path.GetExtension(importedDestinationPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ShortcutInspection? inspection = InspectLink(importedDestinationPath);
        if (inspection is null || inspection.IsMsiAdvertised || inspection.IsVirtualShellTarget)
            return false;

        string? originalDir = string.IsNullOrWhiteSpace(originalSourcePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(originalSourcePath));
        string? destinationDir = Path.GetDirectoryName(Path.GetFullPath(importedDestinationPath));
        bool directoryChanged = !string.IsNullOrWhiteSpace(originalDir) &&
            !string.Equals(originalDir, destinationDir, StringComparison.OrdinalIgnoreCase);

        string? resolvedTarget = ResolveExistingFilesystemTarget(inspection.ExpandedTargetPath, originalDir);
        if (string.IsNullOrWhiteSpace(resolvedTarget))
            return false;

        string targetParentDir = Directory.Exists(resolvedTarget)
            ? resolvedTarget
            : Path.GetDirectoryName(resolvedTarget) ?? string.Empty;

        string? desiredWorkingDir = ComputeDesiredWorkingDirectory(
            inspection.RawWorkingDirectory,
            originalDir,
            targetParentDir);

        bool needsWorkingDirFix = !string.IsNullOrWhiteSpace(desiredWorkingDir) &&
            !string.Equals(ExpandPath(inspection.RawWorkingDirectory), desiredWorkingDir, StringComparison.OrdinalIgnoreCase);

        bool needsTargetFix = !string.Equals(inspection.ExpandedTargetPath, resolvedTarget, StringComparison.OrdinalIgnoreCase);
        bool needsRelPathClear = inspection.HasRelativePath && directoryChanged;

        string? repairedIconPath = ResolveRepairedRelativeIconPath(inspection.IconPath, originalDir);
        bool needsIconFix = repairedIconPath is not null &&
            !string.Equals(inspection.IconPath, repairedIconPath, StringComparison.OrdinalIgnoreCase);

        if (!needsWorkingDirFix && !needsTargetFix && !needsRelPathClear && !needsIconFix)
            return false;

        object? instance = null;
        try
        {
            // Ensure read-only flag does not block in-place shortcut repair.
            FileAttributes attributes = File.GetAttributes(importedDestinationPath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (wasReadOnly)
                File.SetAttributes(importedDestinationPath, attributes & ~FileAttributes.ReadOnly);

            instance = new NativeMethods.ShellLink();
            var link = (NativeMethods.IShellLinkW)instance;
            var persist = (IPersistFile)instance;
            persist.Load(importedDestinationPath, StgmReadWrite);

            if (needsTargetFix || needsRelPathClear)
            {
                Marshal.ThrowExceptionForHR(link.SetPath(resolvedTarget));
                _ = link.SetRelativePath(importedDestinationPath, 0);
            }

            if (needsWorkingDirFix && !string.IsNullOrWhiteSpace(desiredWorkingDir))
                Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(desiredWorkingDir));

            if (needsIconFix && repairedIconPath is not null)
                Marshal.ThrowExceptionForHR(link.SetIconLocation(repairedIconPath, inspection.IconIndex));

            if (inspection.RequiresElevation &&
                instance is NativeMethods.IShellLinkDataList dataList &&
                dataList.GetFlags(out uint currentFlags) >= 0 &&
                (currentFlags & NativeMethods.SLDF_RUNAS_USER) == 0)
            {
                _ = dataList.SetFlags(currentFlags | NativeMethods.SLDF_RUNAS_USER);
            }

            persist.Save(importedDestinationPath, true);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"修复迁入快捷方式失败（已保留原始文件）：{importedDestinationPath}", ex);
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    internal static bool TryReadInternetShortcutUrl(string urlPath, out string url, out string? workingDirectory)
    {
        url = string.Empty;
        workingDirectory = null;
        if (string.IsNullOrWhiteSpace(urlPath) ||
            !File.Exists(urlPath) ||
            !Path.GetExtension(urlPath).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var urlBuffer = new StringBuilder(4096);
            uint len = NativeMethods.GetPrivateProfileString(
                "InternetShortcut",
                "URL",
                string.Empty,
                urlBuffer,
                (uint)urlBuffer.Capacity,
                urlPath);
            if (len == 0) return false;

            string candidate = urlBuffer.ToString().Trim();
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed) ||
                !SafeInternetShortcutSchemes.Contains(parsed.Scheme))
            {
                return false;
            }

            var wdBuffer = new StringBuilder(4096);
            if (NativeMethods.GetPrivateProfileString(
                "InternetShortcut",
                "WorkingDirectory",
                string.Empty,
                wdBuffer,
                (uint)wdBuffer.Capacity,
                urlPath) > 0)
            {
                string expanded = ExpandPath(wdBuffer.ToString());
                if (Directory.Exists(expanded))
                    workingDirectory = expanded;
            }

            url = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveEffectiveWorkingDirectory(
        string rawWorkingDirectory,
        string expandedTargetPath,
        string shortcutPath)
    {
        string expandedWd = ExpandPath(rawWorkingDirectory);
        if (!string.IsNullOrWhiteSpace(expandedWd) && Directory.Exists(expandedWd))
            return expandedWd;

        if (!string.IsNullOrWhiteSpace(expandedTargetPath))
        {
            if (Directory.Exists(expandedTargetPath))
                return expandedTargetPath;
            if (File.Exists(expandedTargetPath))
            {
                string? targetDir = Path.GetDirectoryName(expandedTargetPath);
                if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
                    return targetDir;
            }
        }

        string? shortcutDir = Path.GetDirectoryName(shortcutPath);
        return !string.IsNullOrWhiteSpace(shortcutDir) && Directory.Exists(shortcutDir)
            ? shortcutDir
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? ResolveExistingFilesystemTarget(string expandedTarget, string? originalDir)
    {
        if (string.IsNullOrWhiteSpace(expandedTarget))
            return null;

        if (Path.IsPathRooted(expandedTarget))
        {
            return File.Exists(expandedTarget) || Directory.Exists(expandedTarget)
                ? Path.GetFullPath(expandedTarget)
                : null;
        }

        if (!string.IsNullOrWhiteSpace(originalDir))
        {
            try
            {
                string combined = Path.GetFullPath(Path.Combine(originalDir, expandedTarget));
                if (File.Exists(combined) || Directory.Exists(combined))
                    return combined;
            }
            catch
            {
                // Ignore invalid combined path.
            }
        }
        return null;
    }

    private static string? ComputeDesiredWorkingDirectory(
        string rawWorkingDir,
        string? originalDir,
        string targetParentDir)
    {
        string expandedWd = ExpandPath(rawWorkingDir);
        if (!string.IsNullOrWhiteSpace(expandedWd))
        {
            if (Path.IsPathRooted(expandedWd) && Directory.Exists(expandedWd))
            {
                // If the shortcut explicitly pointed to Desktop even though the executable lives
                // in its own install folder, keep it valid; only anchor if directory is missing.
                return expandedWd;
            }

            if (!Path.IsPathRooted(expandedWd) && !string.IsNullOrWhiteSpace(originalDir))
            {
                try
                {
                    string resolvedRelativeWd = Path.GetFullPath(Path.Combine(originalDir, expandedWd));
                    if (Directory.Exists(resolvedRelativeWd))
                        return resolvedRelativeWd;
                }
                catch
                {
                    // Fall through to targetParentDir.
                }
            }
        }

        return !string.IsNullOrWhiteSpace(targetParentDir) && Directory.Exists(targetParentDir)
            ? targetParentDir
            : null;
    }

    private static string? ResolveRepairedRelativeIconPath(string rawIconPath, string? originalDir)
    {
        if (string.IsNullOrWhiteSpace(rawIconPath) || string.IsNullOrWhiteSpace(originalDir))
            return null;

        string expanded = ExpandPath(rawIconPath);
        if (Path.IsPathRooted(expanded))
            return null;

        try
        {
            string combined = Path.GetFullPath(Path.Combine(originalDir, expanded));
            return File.Exists(combined) ? combined : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool RebindShortcutPrefix(string shortcutPath, string oldDirectoryPrefix, string newDirectoryPrefix)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath) ||
            !Path.GetExtension(shortcutPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ShortcutInspection? inspection = InspectLink(shortcutPath);
        if (inspection is null || inspection.IsMsiAdvertised || inspection.IsVirtualShellTarget)
            return false;

        bool targetRebound = Core.DesktopUpdateMatchRules.TryRewritePathPrefix(
            inspection.ExpandedTargetPath, oldDirectoryPrefix, newDirectoryPrefix, out string newTarget);
        bool workingDirRebound = Core.DesktopUpdateMatchRules.TryRewritePathPrefix(
            ExpandPath(inspection.RawWorkingDirectory), oldDirectoryPrefix, newDirectoryPrefix, out string newWorkingDir);
        bool iconRebound = Core.DesktopUpdateMatchRules.TryRewritePathPrefix(
            ExpandPath(inspection.IconPath), oldDirectoryPrefix, newDirectoryPrefix, out string newIconPath);

        if (!targetRebound && !workingDirRebound && !iconRebound)
            return false;

        object? instance = null;
        try
        {
            FileAttributes attributes = File.GetAttributes(shortcutPath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (wasReadOnly)
                File.SetAttributes(shortcutPath, attributes & ~FileAttributes.ReadOnly);

            instance = new NativeMethods.ShellLink();
            var link = (NativeMethods.IShellLinkW)instance;
            var persist = (IPersistFile)instance;
            persist.Load(shortcutPath, StgmReadWrite);

            if (targetRebound)
            {
                Marshal.ThrowExceptionForHR(link.SetPath(newTarget));
                _ = link.SetRelativePath(shortcutPath, 0);
            }
            if (workingDirRebound)
                Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(newWorkingDir));
            if (iconRebound)
                Marshal.ThrowExceptionForHR(link.SetIconLocation(newIconPath, inspection.IconIndex));

            if (inspection.RequiresElevation &&
                instance is NativeMethods.IShellLinkDataList dataList &&
                dataList.GetFlags(out uint currentFlags) >= 0 &&
                (currentFlags & NativeMethods.SLDF_RUNAS_USER) == 0)
            {
                _ = dataList.SetFlags(currentFlags | NativeMethods.SLDF_RUNAS_USER);
            }

            persist.Save(shortcutPath, true);
            if (wasReadOnly)
                File.SetAttributes(shortcutPath, attributes);

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"重绑快捷方式目录前缀失败：{shortcutPath}", ex);
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    private static string ExpandPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0) return string.Empty;
        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    private static string ReadString(Func<StringBuilder, int, int> getter)
    {
        var buffer = new StringBuilder(32768);
        return getter(buffer, buffer.Capacity) >= 0 ? buffer.ToString().Trim() : string.Empty;
    }
}
