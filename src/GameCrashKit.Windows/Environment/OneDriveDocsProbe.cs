using System.IO;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only probe checking whether <c>MyDocuments</c> points into OneDrive or a reparse point.
/// </summary>
public static class OneDriveDocsProbe
{
    public static OneDriveDocsFact Probe()
    {
        var docsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
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

        var hasReparsePoint = false;
        try
        {
            if (Directory.Exists(docsPath))
            {
                var attrs = File.GetAttributes(docsPath);
                hasReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0;
            }
        }
        catch (Exception)
        {
            // Keep non-fatal; path string check still holds.
        }

        if (isInOneDrive)
        {
            return new OneDriveDocsFact(
                Checked: true,
                IsInOneDrive: true,
                HasReparsePoint: hasReparsePoint,
                NoticeLevel: EnvironmentNoticeLevel.Attention,
                StatusText: "“文档”目录位于 OneDrive 同步范围内；部分游戏写入存档或配置文件时需留意云同步占用。");
        }

        return new OneDriveDocsFact(
            Checked: true,
            IsInOneDrive: false,
            HasReparsePoint: hasReparsePoint,
            NoticeLevel: EnvironmentNoticeLevel.Info,
            StatusText: "“文档”目录未指向 OneDrive。");
    }
}
