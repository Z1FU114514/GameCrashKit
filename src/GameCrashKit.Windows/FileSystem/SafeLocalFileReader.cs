using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameCrashKit.Windows.FileSystem;

public enum SafeFileReadErrorKind
{
    None = 0,
    InvalidPath,
    UnsupportedFileType,
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

        var ext = Path.GetExtension(fullPath);
        if (string.Equals(ext, ".dmp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".mdmp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".hdmp", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(SafeFileReadErrorKind.UnsupportedFileType, "按纯只读轻量化原则不支持读取 .dmp 内存转储文件，请选择文本日志（.log / .txt / .xml / .runtime-xml）。");
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

        // Check ancestor directories and target BEFORE File.Exists(fullPath) so a local junction pointing to a remote share is never touched first.
        if (HasAnyReparsePointInPath(root, fullPath, out var reparseErrorKind, out var reparseMsg))
        {
            return Fail(reparseErrorKind, reparseMsg!);
        }

        if (!File.Exists(fullPath))
        {
            return Fail(SafeFileReadErrorKind.FileNotFound, "目标文件不存在。");
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

            string decoded;
            if (!isTruncated && decodeSpan.Length >= 2 && decodeSpan[0] == 0xFF && decodeSpan[1] == 0xFE)
            {
                try
                {
                    decoded = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
                        .GetString(decodeSpan[2..]);
                }
                catch (DecoderFallbackException)
                {
                    return Fail(SafeFileReadErrorKind.InvalidUtf8Encoding, "文件不是有效的 UTF-16 LE 编码文本，建议在本机另存为 UTF-8 后重试。");
                }
            }
            else if (!isTruncated && decodeSpan.Length >= 2 && decodeSpan[0] == 0xFE && decodeSpan[1] == 0xFF)
            {
                try
                {
                    decoded = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true)
                        .GetString(decodeSpan[2..]);
                }
                catch (DecoderFallbackException)
                {
                    return Fail(SafeFileReadErrorKind.InvalidUtf8Encoding, "文件不是有效的 UTF-16 BE 编码文本，建议在本机另存为 UTF-8 后重试。");
                }
            }
            else
            {
                if (!isTruncated && decodeSpan.Length >= 3 && decodeSpan[0] == 0xEF && decodeSpan[1] == 0xBB && decodeSpan[2] == 0xBF)
                {
                    decodeSpan = decodeSpan[3..];
                }
                else if (isTruncated)
                {
                    // Skip up to 3 UTF-8 continuation bytes (10xxxxxx) at the arbitrary tail-seek boundary.
                    var skip = 0;
                    while (skip < 3 && skip < decodeSpan.Length && (decodeSpan[skip] & 0xC0) == 0x80)
                    {
                        skip++;
                    }

                    decodeSpan = decodeSpan[skip..];
                }

                try
                {
                    var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                    decoded = strictUtf8.GetString(decodeSpan);
                }
                catch (DecoderFallbackException)
                {
                    return Fail(SafeFileReadErrorKind.InvalidUtf8Encoding, "文件不是有效的 UTF-8 或 UTF-16 编码文本，建议在本机另存为 UTF-8 后重试。");
                }
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

    /// <summary>
    /// Validates a directory path purely for fixed-drive locality BEFORE any remote/network probe
    /// and verifies existing ancestor segments contain no reparse points.
    /// </summary>
    internal static bool TryValidateLocalDirectoryWithoutNetworkAccess(
        string? rawPath,
        out string? normalizedFullPath,
        out bool isRemoteOrNonFixed,
        out bool hasReparseAncestor)
    {
        normalizedFullPath = null;
        isRemoteOrNonFixed = false;
        hasReparseAncestor = false;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        var trimmed = rawPath.Trim().Trim('"');
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            isRemoteOrNonFixed = true;
            return false;
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            isRemoteOrNonFixed = true;
            return false;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                isRemoteOrNonFixed = true;
                return false;
            }
        }
        catch (Exception)
        {
            isRemoteOrNonFixed = true;
            return false;
        }

        try
        {
            var relative = fullPath[root.Length..];
            var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                if (!Directory.Exists(current))
                {
                    break;
                }

                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    hasReparseAncestor = true;
                    return false;
                }
            }
        }
        catch (Exception)
        {
            hasReparseAncestor = true;
            return false;
        }

        normalizedFullPath = fullPath;
        return true;
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
                var isLeaf = i == parts.Length - 1;
                if (isLeaf ? !File.Exists(current) && !Directory.Exists(current) : !Directory.Exists(current))
                {
                    break;
                }

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
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
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

    public static bool AreSameLocalFile(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        try
        {
            var fullA = Path.GetFullPath(pathA.Trim());
            var fullB = Path.GetFullPath(pathB.Trim());
            if (string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(fullA) || !File.Exists(fullB))
            {
                return false;
            }

            using var handleA = File.OpenHandle(fullA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var handleB = File.OpenHandle(fullB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handleA, out var infoA) ||
                !GetFileInformationByHandle(handleB, out var infoB))
            {
                return false;
            }

            return infoA.VolumeSerialNumber == infoB.VolumeSerialNumber &&
                   infoA.FileIndexHigh == infoB.FileIndexHigh &&
                   infoA.FileIndexLow == infoB.FileIndexLow;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

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
