using System.IO;
using GameCrashKit.Core.Models;
using GameCrashKit.Windows.FileSystem;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only probe checking whether <c>MyDocuments</c> points into OneDrive or a reparse/network location,
/// using <c>DoNotVerify</c> and pre-checking fixed-drive locality before any directory access.
/// </summary>
public static class OneDriveDocsProbe
{
    public static OneDriveDocsFact Probe()
    {
        var docsPath = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.MyDocuments,
            System.Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(docsPath))
        {
            return new OneDriveDocsFact(
                Checked: false,
                IsInOneDrive: false,
                HasReparsePoint: false,
                NoticeLevel: EnvironmentNoticeLevel.Unavailable,
                StatusText: "无法读取“文档”目录路径。");
        }

        var segments = docsPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var isInOneDrive = segments.Any(static s => s.Contains("OneDrive", StringComparison.OrdinalIgnoreCase));

        if (!SafeLocalFileReader.TryValidateLocalDirectoryWithoutNetworkAccess(
                docsPath,
                out var normalizedPath,
                out var isRemoteOrNonFixed,
                out var hasReparseAncestor))
        {
            if (isRemoteOrNonFixed)
            {
                return new OneDriveDocsFact(
                    Checked: true,
                    IsInOneDrive: isInOneDrive,
                    HasReparsePoint: true,
                    NoticeLevel: EnvironmentNoticeLevel.Attention,
                    StatusText: "“文档”目录重定向至网络或非本地固定磁盘（未执行远程目录探测）。");
            }

            if (hasReparseAncestor)
            {
                return new OneDriveDocsFact(
                    Checked: true,
                    IsInOneDrive: isInOneDrive,
                    HasReparsePoint: true,
                    NoticeLevel: EnvironmentNoticeLevel.Attention,
                    StatusText: "“文档”或其上层目录包含重解析点/联接（未深入探测目标位置）。");
            }
        }

        if (isInOneDrive)
        {
            return new OneDriveDocsFact(
                Checked: true,
                IsInOneDrive: true,
                HasReparsePoint: hasReparseAncestor,
                NoticeLevel: EnvironmentNoticeLevel.Attention,
                StatusText: "“文档”目录位于 OneDrive 同步范围内；部分游戏写入存档或配置文件时需留意云同步占用。");
        }

        var dirExists = normalizedPath is not null && Directory.Exists(normalizedPath);
        return new OneDriveDocsFact(
            Checked: dirExists || normalizedPath is not null,
            IsInOneDrive: false,
            HasReparsePoint: hasReparseAncestor,
            NoticeLevel: EnvironmentNoticeLevel.Info,
            StatusText: "“文档”目录位于本地固定磁盘，未指向 OneDrive。");
    }
}
