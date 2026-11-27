[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.2.0-alpha.3",
    [string]$OutputDir = "",
    [string]$WorkDir = "",
    [string]$LocalZipPath = "",
    [string]$LocalChecksumPath = "",
    [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Get-Sha256HexLower([string]$filePath) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($filePath)
        try {
            $hashBytes = $sha.ComputeHash($fs)
            return ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $fs.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

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
        $cleaned = foreach ($line in $zoneLines) {
            $item = ($line -replace "`0", "").Trim()
            if ($item -eq "") { continue }
            if ($item -match '^ReferrerUrl=(?!https?://)') {
                "ReferrerUrl=<local-archive>"
            }
            else {
                $item
            }
        }
        $joined = $cleaned -join " | "
        if ([string]::IsNullOrWhiteSpace($joined)) { return "EMPTY_ADS" }
        return $joined
    }
    catch {
        return "NONE"
    }
}

$script:OwnedTestPids = New-Object System.Collections.Generic.HashSet[int]

function Register-OwnedTestPid([int]$processId) {
    if ($processId -gt 0) {
        $null = $script:OwnedTestPids.Add($processId)
    }
}

function Stop-OwnedTestProcesses {
    foreach ($ownedPid in @($script:OwnedTestPids)) {
        try {
            $proc = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
            if ($null -ne $proc -and $proc.ProcessName -eq "GameCrashKit.App") {
                Stop-Process -Id $ownedPid -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # Ignore already exited process
        }
        $null = $script:OwnedTestPids.Remove($ownedPid)
    }
}

try {
    Record-Line "============================================================"
    Record-Line ("GameCrash Kit First-Run Trust & SmartScreen Probe | Target: " + $VersionLabel)
    Record-Line ("TimestampDate: " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd"))
    Record-Line "============================================================"

    # 1. OS & Standard User Environment (never emit username or exact clock)
    $os = Get-CimInstance Win32_OperatingSystem
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $culture = [System.Globalization.CultureInfo]::CurrentUICulture.Name

    $regExplorer = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer" -Name "SmartScreenEnabled" -ErrorAction SilentlyContinue).SmartScreenEnabled
    $regPolicyEnable = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "EnableSmartScreen" -ErrorAction SilentlyContinue).EnableSmartScreen
    $regPolicyLevel = (Get-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name "ShellSmartScreenLevel" -ErrorAction SilentlyContinue).ShellSmartScreenLevel
    $regWebContent = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\AppHost" -Name "EnableWebContentEvaluation" -ErrorAction SilentlyContinue).EnableWebContentEvaluation

    Record-Line ("[ENV] OS={0} (Build {1}) | UICulture={2} | IsAdmin={3}" -f $os.Caption, $os.BuildNumber, $culture, $isAdmin)
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
        Record-Line ("[DOWNLOAD] Using staged archive file: " + (Split-Path $LocalZipPath -Leaf))
    }
    else {
        throw "No ZIP source provided and -SkipDownload was specified."
    }

    # Strict hard gate BEFORE any extraction, MotW tagging, or launch
    if (-not (Test-Path $sumPath)) {
        Record-Line ("[SHA256] Checksum file missing: " + $sumName + " | Match=False")
        throw ("Required SHA-256 checksum file missing: " + $sumPath)
    }

    $rawSumLine = (Get-Content $sumPath -ErrorAction Stop | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($rawSumLine) -or $rawSumLine.Trim() -notmatch '^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>\S+)$') {
        Record-Line "[SHA256] Malformed checksum line | Match=False"
        throw "Checksum file format invalid (expected '<64-hex-sha256>  <filename>')."
    }

    $expectedHash = $Matches["hash"].ToLowerInvariant()
    $expectedFile = Split-Path $Matches["file"] -Leaf
    $actualHash = Get-Sha256HexLower $zipPath
    $zipSize = (Get-Item $zipPath).Length
    $fileMatch = [string]::Equals($expectedFile, $zipName, [System.StringComparison]::OrdinalIgnoreCase)
    $hashMatch = $fileMatch -and ($actualHash -eq $expectedHash)

    Record-Line ("[SHA256] ZipBytes={0} | TargetFile={1} | ExpectedFile={2} | Actual={3} | Expected={4} | Match={5}" -f `
        $zipSize, $zipName, $expectedFile, $actualHash, $expectedHash, $hashMatch)

    if (-not $hashMatch) {
        throw "SHA-256 verification failed before extraction; aborting with zero launches."
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

    # 6. Non-blocking ShellExecute launch attempt under MotW (never kills SmartScreen or other user windows)
    $smartScreenBeforePids = @(Get-Process -Name "smartscreen" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $motwLaunchError = "None"
    $motwWindowReady = $false
    $motwExited = $false
    $motwExitCode = ""
    $motwShellBlockedOrPending = $false
    $smartScreenStatus = "NotTriggered"

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

    if (-not $completedInWindow) {
        $motwShellBlockedOrPending = $true
        $smartScreenStatus = "WaitingForUser"
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
                $motwPid = [int]$outItems[0]
                Register-OwnedTestPid $motwPid
                $motwProc = Get-Process -Id $motwPid -ErrorAction SilentlyContinue
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
        if ($smartScreenDuringPids.Count -gt $smartScreenBeforePids.Count) {
            $smartScreenStatus = "Observed"
        }
    }

    Record-Line ("[LAUNCH-MOTW] SmartScreenCountBefore={0} | SmartScreenCountDuring={1} | SmartScreenStatus={2} | ShellBlockedPending={3} | MainWindowReady={4} | Exited={5} | ExitCode={6} | Error={7}" -f `
        $smartScreenBeforePids.Count, $smartScreenDuringPids.Count, $smartScreenStatus, $motwShellBlockedOrPending, $motwWindowReady, $motwExited, $motwExitCode, $motwLaunchError)

    Stop-OwnedTestProcesses
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
    Register-OwnedTestPid $unblockedPid
    for ($u = 0; $u -lt 20; $u++) {
        Start-Sleep -Milliseconds 500
        $unblockedProc.Refresh()
        if ($unblockedProc.HasExited) { break }
        if ($unblockedProc.MainWindowHandle -ne [IntPtr]::Zero) {
            $unblockedReady = $true
            break
        }
    }
    Record-Line ("[LAUNCH-UNBLOCKED] AppExeMotW={0} | MainWindowReady={1} | Exited={2}" -f `
        $unblockedMotw, $unblockedReady, $unblockedProc.HasExited)
    Stop-OwnedTestProcesses

    $reportPath = Join-Path $OutputDir "firstrun-trust-report.txt"
    $flagPath = Join-Path $OutputDir "firstrun-trust-done.flag"
    [System.IO.File]::WriteAllLines($reportPath, $reportLines, (New-Object System.Text.UTF8Encoding($true)))
    [System.IO.File]::WriteAllText($flagPath, ("DONE " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd") + "`r`n"), [System.Text.Encoding]::ASCII)
    [Environment]::Exit(0)
}
catch {
    $errMsg = $_.Exception.Message
    Record-Line ("[FAIL] " + $errMsg)
    $reportPath = Join-Path $OutputDir "firstrun-trust-report.txt"
    $flagPath = Join-Path $OutputDir "firstrun-trust-done.flag"
    [System.IO.File]::WriteAllLines($reportPath, $reportLines, (New-Object System.Text.UTF8Encoding($true)))
    [System.IO.File]::WriteAllText($flagPath, ("FAIL " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd") + "`r`n"), [System.Text.Encoding]::ASCII)
    Stop-OwnedTestProcesses
    [Environment]::Exit(1)
}
