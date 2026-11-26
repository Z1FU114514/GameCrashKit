using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameCrashKit.Windows.FileSystem;

public enum SafeFileReadErrorKind
{
    None = 0,
    InvalidPath,
    NetworkOrDevicePathRejected,
    NonFixedDriveRejected,
    ReparsePointRejected,
    HandlePathMismatch,
    FileNotFound,
    AccessDenied,
    InvalidUtf8Encoding,
    FileChangedDuringRead,
    ReadFailed
}

public sealed record SafeFileReadResult(
    bool Success,
    SafeFileReadErrorKind ErrorKind,
    string? ErrorMessage,
    string? Content,
    long FileSizeBytes,
    long ReadStartByteOffset,
    long ReadEndByteOffset,
    bool IsTruncated,
    DateTimeOffset? LastWriteTimeUtc);

/// <summary>
/// Hardened read-only tail reader for single local log files.
/// Rejects UNC paths, Win32 device paths, non-fixed drives, reparse points/junctions/symlinks,
/// handle-target escapes, non-UTF-8 files, and files modified mid-read.
/// </summary>
public static class SafeLocalFileReader
{
    public const int MaxTailReadBytes = 2 * 1024 * 1024; // 2 MiB
    private const uint FileNameNormalized = 0x0;

    public static SafeFileReadResult ReadTailUtf8(
        string filePath,
        string? requiredRootDirectory = null,
        int maxBytes = MaxTailReadBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Fail(SafeFileReadErrorKind.InvalidPath, "文件路径为空。");
        }

        var trimmed = filePath.Trim().Trim('"');

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return Fail(SafeFileReadErrorKind.NetworkOrDevicePathRejected, "拒绝读取网络共享 (UNC) 或 Win32 设备路径。");
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return Fail(SafeFileReadErrorKind.InvalidPath, "仅支持绝对本地路径。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex)
        {
            return Fail(SafeFileReadErrorKind.InvalidPath, $"路径无效：{ex.Message}");
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Fail(SafeFileReadErrorKind.NetworkOrDevicePathRejected, "拒绝非本地磁盘路径。");
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                return Fail(SafeFileReadErrorKind.NonFixedDriveRejected, $"仅允许读取本地固定磁盘文件（当前驱动器类型：{drive.DriveType}）。");
            }
        }
        catch (Exception ex)
        {
            return Fail(SafeFileReadErrorKind.InvalidPath, $"无法识别目标磁盘：{ex.Message}");
        }

        if (!File.Exists(fullPath))
        {
            return Fail(SafeFileReadErrorKind.FileNotFound, "目标文件不存在。");
        }

        // Check every directory segment below the drive root AND the file itself for reparse points (symlink/junction).
        if (HasAnyReparsePointInPath(root, fullPath, out var reparseErrorKind, out var reparseMsg))
        {
            return Fail(reparseErrorKind, reparseMsg!);
        }

        string? normalizedRequiredRoot = null;
        if (!string.IsNullOrWhiteSpace(requiredRootDirectory))
        {
            normalizedRequiredRoot = Path.GetFullPath(requiredRootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        try
        {
            using var handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            var finalPath = GetFinalPathFromHandle(handle);
            if (finalPath is null)
            {
                return Fail(SafeFileReadErrorKind.HandlePathMismatch, "无法验证已打开文件句柄的最终规范路径。");
            }

            if (!string.Equals(finalPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(SafeFileReadErrorKind.HandlePathMismatch, "打开句柄的最终路径与预期不一致，已安全中止读取。");
            }

            if (normalizedRequiredRoot is not null &&
                !finalPath.StartsWith(normalizedRequiredRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(SafeFileReadErrorKind.HandlePathMismatch, "目标文件最终路径超出允许目录范围。");
            }

            var beforeInfo = new FileInfo(fullPath);
            var beforeLength = RandomAccess.GetLength(handle);
            var beforeWriteUtc = beforeInfo.LastWriteTimeUtc;

            var isTruncated = beforeLength > maxBytes;
            var readStart = isTruncated ? beforeLength - maxBytes : 0;
            var bytesToRead = checked((int)(beforeLength - readStart));

            var buffer = new byte[bytesToRead];
            var totalRead = 0;
            while (totalRead < bytesToRead)
            {
                var n = RandomAccess.Read(handle, buffer.AsSpan(totalRead), readStart + totalRead);
                if (n == 0)
                {
                    break;
                }

                totalRead += n;
            }

            var afterLength = RandomAccess.GetLength(handle);
            var afterInfo = new FileInfo(fullPath);
            var afterWriteUtc = afterInfo.LastWriteTimeUtc;

            if (afterLength != beforeLength || afterWriteUtc != beforeWriteUtc)
            {
                return Fail(SafeFileReadErrorKind.FileChangedDuringRead, "日志文件在读取过程中发生变化，请确认游戏已退出后重新选择。");
            }

            var actualEnd = readStart + totalRead;
            var decodeSpan = buffer.AsSpan(0, totalRead);

            if (!isTruncated)
            {
                // Strip UTF-8 BOM if present at byte 0.
                if (decodeSpan.Length >= 3 && decodeSpan[0] == 0xEF && decodeSpan[1] == 0xBB && decodeSpan[2] == 0xBF)
                {
                    decodeSpan = decodeSpan[3..];
                }
            }
            else
            {
                // Skip up to 3 UTF-8 continuation bytes (10xxxxxx) at the arbitrary tail-seek boundary.
                var skip = 0;
                while (skip < 3 && skip < decodeSpan.Length && (decodeSpan[skip] & 0xC0) == 0x80)
                {
                    skip++;
                }

                decodeSpan = decodeSpan[skip..];
            }

            string decoded;
            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                decoded = strictUtf8.GetString(decodeSpan);
            }
            catch (DecoderFallbackException)
            {
                return Fail(SafeFileReadErrorKind.InvalidUtf8Encoding, "文件不是有效的 UTF-8 编码文本，已拒绝解析。");
            }

            if (isTruncated)
            {
                // Drop the first potentially partial line so parsing always begins on a full line.
                var firstNewline = decoded.IndexOf('\n');
                if (firstNewline >= 0 && firstNewline + 1 < decoded.Length)
                {
                    decoded = decoded[(firstNewline + 1)..];
                }
            }

            return new SafeFileReadResult(
                Success: true,
                ErrorKind: SafeFileReadErrorKind.None,
                ErrorMessage: null,
                Content: decoded,
                FileSizeBytes: beforeLength,
                ReadStartByteOffset: readStart,
                ReadEndByteOffset: actualEnd,
                IsTruncated: isTruncated,
                LastWriteTimeUtc: new DateTimeOffset(beforeWriteUtc, TimeSpan.Zero));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(SafeFileReadErrorKind.AccessDenied, $"无权限读取目标文件：{ex.Message}");
        }
        catch (FileNotFoundException)
        {
            return Fail(SafeFileReadErrorKind.FileNotFound, "目标文件不存在。");
        }
        catch (IOException ex)
        {
            return Fail(SafeFileReadErrorKind.ReadFailed, $"读取文件失败：{ex.Message}");
        }
    }

    internal static bool HasAnyReparsePointInPath(
        string driveRoot,
        string fullPath,
        out SafeFileReadErrorKind errorKind,
        out string? errorMessage)
    {
        errorKind = SafeFileReadErrorKind.None;
        errorMessage = null;

        try
        {
            var relative = fullPath[driveRoot.Length..];
            var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = driveRoot;

            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    errorKind = SafeFileReadErrorKind.ReparsePointRejected;
                    errorMessage = "检测到目标文件或祖先目录包含符号链接/目录联接 (ReparsePoint)，已拒绝读取。";
                    return true;
                }
            }

            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorKind = SafeFileReadErrorKind.AccessDenied;
            errorMessage = $"无权限校验目录属性：{ex.Message}";
            return true;
        }
        catch (IOException ex)
        {
            errorKind = SafeFileReadErrorKind.ReadFailed;
            errorMessage = $"校验路径属性失败：{ex.Message}";
            return true;
        }
    }

    private static string? GetFinalPathFromHandle(SafeFileHandle handle)
    {
        var buffer = new char[512];
        var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized);
        if (len == 0)
        {
            return null;
        }

        if (len >= buffer.Length)
        {
            buffer = new char[len + 1];
            len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized);
            if (len == 0 || len >= buffer.Length)
            {
                return null;
            }
        }

        var path = new string(buffer, 0, (int)len);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    private static SafeFileReadResult Fail(SafeFileReadErrorKind kind, string message) =>
        new(false, kind, message, null, 0, 0, 0, false, null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
