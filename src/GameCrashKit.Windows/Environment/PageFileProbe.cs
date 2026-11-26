using System.Management;
using GameCrashKit.Core.Models;
using Microsoft.Win32;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only probe inspecting pagefile configuration and summing allocated capacity across all pagefiles.
/// </summary>
public static class PageFileProbe
{
    private const string MemoryManagementSubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    public static PageFileFact Probe()
    {
        var isSystemManaged = false;
        var hasRegistryConfig = false;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(MemoryManagementSubKey, writable: false);
            if (key?.GetValue("PagingFiles") is string[] entries)
            {
                hasRegistryConfig = entries.Length > 0;
                isSystemManaged = entries.Any(static e => e.Trim().EndsWith(" 0 0", StringComparison.Ordinal));
            }
        }
        catch (Exception)
        {
            // Fall through to WMI usage query.
        }

        var totalAllocatedMb = 0;
        var count = 0;

        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT AllocatedBaseSize FROM Win32_PageFileUsage");

        using var collection = searcher.Get();
        foreach (ManagementBaseObject obj in collection)
        {
            using (obj)
            {
                count++;
                if (obj["AllocatedBaseSize"] is uint u)
                {
                    totalAllocatedMb += (int)u;
                }
                else if (obj["AllocatedBaseSize"] is int i && i > 0)
                {
                    totalAllocatedMb += i;
                }
            }
        }

        var isDisabled = count == 0 && !hasRegistryConfig;
        if (count == 0 && hasRegistryConfig)
        {
            // Configured in registry but usage reported 0 active files (could be effectively disabled if empty)
            isDisabled = totalAllocatedMb == 0;
        }

        if (isDisabled)
        {
            return new PageFileFact(
                Checked: true,
                IsDisabled: true,
                TotalAllocatedMb: 0,
                PageFileCount: count,
                IsSystemManaged: false,
                NoticeLevel: EnvironmentNoticeLevel.Attention,
                StatusText: "未检测到活跃虚拟内存（页面文件可能已手动禁用）。");
        }

        if (totalAllocatedMb < 4096)
        {
            return new PageFileFact(
                Checked: true,
                IsDisabled: false,
                TotalAllocatedMb: totalAllocatedMb,
                PageFileCount: count,
                IsSystemManaged: isSystemManaged,
                NoticeLevel: EnvironmentNoticeLevel.Attention,
                StatusText: $"已分配虚拟内存总量较小：{totalAllocatedMb} MB（共 {count} 个页面文件）。");
        }

        var modeText = isSystemManaged ? "系统托管" : "自定义";
        return new PageFileFact(
            Checked: true,
            IsDisabled: false,
            TotalAllocatedMb: totalAllocatedMb,
            PageFileCount: count,
            IsSystemManaged: isSystemManaged,
            NoticeLevel: EnvironmentNoticeLevel.Info,
            StatusText: $"已配置 {totalAllocatedMb} MB（{count} 个页面文件，{modeText}）。");
    }
}
