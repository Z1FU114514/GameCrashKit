[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.2.0-alpha.2",
    [string]$OutputDir = "",
    [string]$WorkDir = "",
    [string]$LocalZipPath = "",
    [string]$LocalChecksumPath = "",
    [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path ([Environment]::GetFolderPath("Desktop")) "GCK-TrustOutput"
}
if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "GameCrashKit-TrustCheck"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$reportLines = New-Object System.Collections.Generic.List[string]
function Record-Line([string]$line) {
    Write-Host $line
    $reportLines.Add($line)
}

function Get-ZoneIdentifierSummary([string]$filePath) {
    if (-not (Test-Path $filePath)) {
        return "MISSING_FILE"
    }
    try {
        $zoneLines = Get-Content -Path $filePath -Stream "Zone.Identifier" -ErrorAction Stop
        $joined = ($zoneLines | ForEach-Object { ($_ -replace "`0", "").Trim() } | Where-Object { $_ -ne "" }) -join " | "
        if ([string]::IsNullOrWhiteSpace($joined)) { return "EMPTY_ADS" }
        return $joined
    }
    catch {
        return "NONE"
    }
}

function Stop-TestProcesses {
    Get-Process -Name "GameCrashKit.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

try {
    Record-Line "============================================================"
    Record-Line ("GameCrash Kit First-Run Trust & SmartScreen Probe | Target: " + $VersionLabel)
    Record-Line ("TimestampLocal: " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
    Record-Line "============================================================"

    # 1. OS & Standard User Environment
    $os = Get-CimInstance Win32_OperatingSystem
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $culture = [System.Globalization.CultureInfo]::CurrentUICulture.Name

    $regExplorer = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer" -Name "SmartScreenEnabled" -ErrorAction SilentlyContinue).SmartScreenEnabled
    $regPolicyEnable = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "EnableSmartScreen" -ErrorAction SilentlyContinue).EnableSmartScreen
    $regPolicyLevel = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "ShellSmartScreenLevel" -ErrorAction SilentlyContinue).ShellSmartScreenLevel
    $regWebContent = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost" -Name "EnableWebContentEvaluation" -ErrorAction SilentlyContinue).EnableWebContentEvaluation

    Record-Line ("[ENV] OS={0} (Build {1}) | UICulture={2} | User={3} | IsAdmin={4}" -f $os.Caption, $os.BuildNumber, $culture, [Environment]::UserName, $isAdmin)
    Record-Line ("[ENV] SmartScreenEnabled={0} | Policy.EnableSmartScreen={1} | Policy.Level={2} | AppHost.EnableWebContentEvaluation={3}" -f `
        $(if ($null -eq $regExplorer) { "<unset>" } else { $regExplorer }), `
        $(if ($null -eq $regPolicyEnable) { "<unset>" } else { $regPolicyEnable }), `
        $(if ($null -eq $regPolicyLevel) { "<unset>" } else { $regPolicyLevel }), `
        $(if ($null -eq $regWebContent) { "<unset>" } else { $regWebContent }))

    # 2. Download from GitHub Releases or stage local copy
    $zipName = "GameCrashKit-$VersionLabel-win-x64.zip"
    $sumName = "GameCrashKit-$VersionLabel-win-x64.SHA256SUMS.txt"
    $downloadDir = Join-Path $WorkDir "download"
    New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
    $zipPath = Join-Path $downloadDir $zipName
    $sumPath = Join-Path $downloadDir $sumName

    $releaseTagUrl = "https://github.com/Z1FU114514/GameCrashKit/releases/tag/$VersionLabel"
    $zipUrl = "https://github.com/Z1FU114514/GameCrashKit/releases/download/$VersionLabel/$zipName"
    $sumUrl = "https://github.com/Z1FU114514/GameCrashKit/releases/download/$VersionLabel/$sumName"

    if (-not $SkipDownload -and [string]::IsNullOrWhiteSpace($LocalZipPath)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        Record-Line ("[DOWNLOAD] Fetching checksum from " + $sumUrl)
        Invoke-WebRequest -UseBasicParsing -Uri $sumUrl -OutFile $sumPath -TimeoutSec 60
        Record-Line ("[DOWNLOAD] Fetching portable ZIP from " + $zipUrl)
        Invoke-WebRequest -UseBasicParsing -Uri $zipUrl -OutFile $zipPath -TimeoutSec 180
    }
    elseif (-not [string]::IsNullOrWhiteSpace($LocalZipPath)) {
        if ([System.IO.Path]::GetFullPath($LocalZipPath) -ne [System.IO.Path]::GetFullPath($zipPath)) {
            Copy-Item -Path $LocalZipPath -Destination $zipPath -Force
        }
        if (-not [string]::IsNullOrWhiteSpace($LocalChecksumPath) -and (Test-Path $LocalChecksumPath)) {
            if ([System.IO.Path]::GetFullPath($LocalChecksumPath) -ne [System.IO.Path]::GetFullPath($sumPath)) {
                Copy-Item -Path $LocalChecksumPath -Destination $sumPath -Force
            }
        }
        Record-Line ("[DOWNLOAD] Using staged archive: " + $LocalZipPath)
    }
    else {
        throw "No ZIP source provided and -SkipDownload was specified."
    }

    $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipSize = (Get-Item $zipPath).Length
    if (Test-Path $sumPath) {
        $expectedHash = ((Get-Content $sumPath -TotalCount 1).Trim() -split '\s+')[0].ToLowerInvariant()
        $hashMatch = ($actualHash -eq $expectedHash)
        Record-Line ("[SHA256] ZipBytes={0} | Actual={1} | Expected={2} | Match={3}" -f $zipSize, $actualHash, $expectedHash, $hashMatch)
    }
    else {
        Record-Line ("[SHA256] ZipBytes={0} | Actual={1} | Expected=<missing>" -f $zipSize, $actualHash)
    }

    # 3. Attach authentic Mark of the Web (Zone.Identifier ADS) onto the downloaded ZIP
    $zoneBody = "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=$releaseTagUrl`r`nHostUrl=$zipUrl`r`n"
    Set-Content -Path $zipPath -Stream "Zone.Identifier" -Value $zoneBody -Encoding Ascii
    $zipMotw = Get-ZoneIdentifierSummary $zipPath
    Record-Line ("[MOTW-ZIP] Zone.Identifier on ZIP: " + $zipMotw)

    # 4a. Method A: Windows Shell / File Explorer ("Extract All" equivalent via Shell.Application)
    $explorerDir = Join-Path $WorkDir "extracted-explorer"
    if (Test-Path $explorerDir) { Remove-Item $explorerDir -Recurse -Force }
    New-Item -ItemType Directory -Path $explorerDir -Force | Out-Null

    $shell = New-Object -ComObject Shell.Application
    $srcNs = $shell.NameSpace($zipPath)
    $dstNs = $shell.NameSpace($explorerDir)
    # 0x14 = 4 (no progress UI) + 16 (yes to all)
    $dstNs.CopyHere($srcNs.Items(), 20)

    $explorerExe = Join-Path $explorerDir "GameCrashKit.App.exe"
    $explorerCore = Join-Path $explorerDir "GameCrashKit.Core.dll"
    $prevCount = -1
    $stableTicks = 0
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Milliseconds 500
        $curCount = @(Get-ChildItem -Path $explorerDir -Recurse -File -ErrorAction SilentlyContinue).Count
        if ((Test-Path $explorerExe) -and $curCount -gt 5 -and $curCount -eq $prevCount) {
            $stableTicks++
            if ($stableTicks -ge 4) { break }
        }
        else {
            $stableTicks = 0
        }
        $prevCount = $curCount
    }

    $explorerFileCount = @(Get-ChildItem -Path $explorerDir -Recurse -File -ErrorAction SilentlyContinue).Count
    $explorerExeMotw = Get-ZoneIdentifierSummary $explorerExe
    $explorerCoreMotw = Get-ZoneIdentifierSummary $explorerCore
    Record-Line ("[EXTRACT-EXPLORER] Files={0} | AppExeMotW={1} | CoreDllMotW={2}" -f $explorerFileCount, $explorerExeMotw, $explorerCoreMotw)

    # 4b. Method B: PowerShell Expand-Archive
    $pwshDir = Join-Path $WorkDir "extracted-pwsh"
    if (Test-Path $pwshDir) { Remove-Item $pwshDir -Recurse -Force }
    Expand-Archive -Path $zipPath -DestinationPath $pwshDir -Force

    $pwshExe = Join-Path $pwshDir "GameCrashKit.App.exe"
    $pwshCore = Join-Path $pwshDir "GameCrashKit.Core.dll"
    $pwshFileCount = @(Get-ChildItem -Path $pwshDir -Recurse -File -ErrorAction SilentlyContinue).Count
    $pwshExeMotw = Get-ZoneIdentifierSummary $pwshExe
    $pwshCoreMotw = Get-ZoneIdentifierSummary $pwshCore
    Record-Line ("[EXTRACT-PWSH] Files={0} | AppExeMotW={1} | CoreDllMotW={2}" -f $pwshFileCount, $pwshExeMotw, $pwshCoreMotw)

    # 5. Authenticode signature check
    $sig = Get-AuthenticodeSignature -FilePath $explorerExe
    $signerSubject = if ($null -ne $sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "<none>" }
    Record-Line ("[AUTHENTICODE] Status={0} | Signer={1}" -f $sig.Status, $signerSubject)

    # 6. Non-blocking ShellExecute launch attempt under MotW (observes SmartScreen blocking without hanging)
    Stop-TestProcesses
    $smartScreenBeforePids = @(Get-Process -Name "smartscreen" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $motwLaunchError = "None"
    $motwWindowReady = $false
    $motwExited = $false
    $motwExitCode = ""
    $motwShellBlockedOrPending = $false

    $psRunner = [PowerShell]::Create()
    $null = $psRunner.AddScript({
        param($exePath, $workDir)
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exePath
        $psi.WorkingDirectory = $workDir
        $psi.UseShellExecute = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $p) { return 0 }
        return $p.Id
    }).AddArgument($explorerExe).AddArgument($explorerDir)

    $asyncResult = $psRunner.BeginInvoke()
    $completedInWindow = $asyncResult.AsyncWaitHandle.WaitOne(8000)
    $smartScreenDuringPids = @(Get-Process -Name "smartscreen" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $liveAppDuring = @(Get-Process -Name "GameCrashKit.App" -ErrorAction SilentlyContinue).Count

    if (-not $completedInWindow) {
        $motwShellBlockedOrPending = $true
        $motwLaunchError = "ShellExecuteBlockedPendingSmartScreen(>8000ms)"
    }
    else {
        try {
            $outItems = $psRunner.EndInvoke($asyncResult)
            if ($psRunner.HadErrors -and $psRunner.Streams.Error.Count -gt 0) {
                $errRec = $psRunner.Streams.Error[0]
                $motwLaunchError = ($errRec.Exception.GetType().Name + ": " + $errRec.Exception.Message).Replace("`r", " ").Replace("`n", " ")
            }
            elseif ($outItems.Count -gt 0 -and [int]$outItems[0] -gt 0) {
                $motwProc = Get-Process -Id ([int]$outItems[0]) -ErrorAction SilentlyContinue
                for ($w = 0; $w -lt 12; $w++) {
                    Start-Sleep -Milliseconds 500
                    if ($null -ne $motwProc) {
                        $motwProc.Refresh()
                        if ($motwProc.HasExited) {
                            $motwExited = $true
                            $motwExitCode = [string]$motwProc.ExitCode
                            break
                        }
                        if ($motwProc.MainWindowHandle -ne [IntPtr]::Zero) {
                            $motwWindowReady = $true
                            break
                        }
                    }
                }
            }
        }
        catch {
            $motwLaunchError = ($_.Exception.GetType().Name + ": " + $_.Exception.Message).Replace("`r", " ").Replace("`n", " ")
        }
        $smartScreenDuringPids = @(Get-Process -Name "smartscreen" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $liveAppDuring = @(Get-Process -Name "GameCrashKit.App" -ErrorAction SilentlyContinue).Count
    }

    $newSmartScreenPids = @($smartScreenDuringPids | Where-Object { $smartScreenBeforePids -notcontains $_ })
    Record-Line ("[LAUNCH-MOTW] SmartScreenCountBefore={0} | SmartScreenCountDuring={1} | ShellBlockedPending={2} | AppProcessesDuring={3} | MainWindowReady={4} | Exited={5} | ExitCode={6} | Error={7}" -f `
        $smartScreenBeforePids.Count, $smartScreenDuringPids.Count, $motwShellBlockedOrPending, $liveAppDuring, $motwWindowReady, $motwExited, $motwExitCode, $motwLaunchError)

    Stop-TestProcesses
    foreach ($ssPid in $newSmartScreenPids) {
        Stop-Process -Id $ssPid -Force -ErrorAction SilentlyContinue
    }
    if ($completedInWindow) {
        $psRunner.Dispose()
    }
    else {
        $null = $psRunner.BeginStop($null, $null)
    }

    # 7. Unblocked recovery verification
    $unblockedDir = Join-Path $WorkDir "extracted-unblocked"
    if (Test-Path $unblockedDir) { Remove-Item $unblockedDir -Recurse -Force }
    Copy-Item -Path $explorerDir -Destination $unblockedDir -Recurse -Force
    Get-ChildItem -Path $unblockedDir -Recurse -File | Unblock-File

    $unblockedExe = Join-Path $unblockedDir "GameCrashKit.App.exe"
    $unblockedMotw = Get-ZoneIdentifierSummary $unblockedExe
    $unblockedReady = $false
    $unblockedPid = 0
    $psiUnblocked = New-Object System.Diagnostics.ProcessStartInfo
    $psiUnblocked.FileName = $unblockedExe
    $psiUnblocked.WorkingDirectory = $unblockedDir
    $psiUnblocked.UseShellExecute = $true
    $unblockedProc = [System.Diagnostics.Process]::Start($psiUnblocked)
    $unblockedPid = $unblockedProc.Id
    for ($u = 0; $u -lt 20; $u++) {
        Start-Sleep -Milliseconds 500
        $unblockedProc.Refresh()
        if ($unblockedProc.HasExited) { break }
        if ($unblockedProc.MainWindowHandle -ne [IntPtr]::Zero) {
            $unblockedReady = $true
            break
        }
    }
    Record-Line ("[LAUNCH-UNBLOCKED] AppExeMotW={0} | PID={1} | MainWindowReady={2} | Exited={3}" -f `
        $unblockedMotw, $unblockedPid, $unblockedReady, $unblockedProc.HasExited)
    Stop-TestProcesses

    $reportPath = Join-Path $OutputDir "firstrun-trust-report.txt"
    $flagPath = Join-Path $OutputDir "firstrun-trust-done.flag"
    [System.IO.File]::WriteAllLines($reportPath, $reportLines, (New-Object System.Text.UTF8Encoding($true)))
    [System.IO.File]::WriteAllText($flagPath, ("DONE " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "`r`n"), [System.Text.Encoding]::ASCII)
    [Environment]::Exit(0)
}
catch {
    $errMsg = $_.Exception.ToString()
    Record-Line ("[FAIL] " + $errMsg)
    $reportPath = Join-Path $OutputDir "firstrun-trust-report.txt"
    $flagPath = Join-Path $OutputDir "firstrun-trust-done.flag"
    [System.IO.File]::WriteAllLines($reportPath, $reportLines, (New-Object System.Text.UTF8Encoding($true)))
    [System.IO.File]::WriteAllText($flagPath, ("FAIL " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "`r`n"), [System.Text.Encoding]::ASCII)
    Stop-TestProcesses
    exit 1
}
