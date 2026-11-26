[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.1.0-alpha.1"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path $repoRoot "publish"
$zipName = "GameCrashKit-$VersionLabel-win-x64.zip"
$sumName = "GameCrashKit-$VersionLabel-win-x64.SHA256SUMS.txt"
$zipPath = Join-Path $publishRoot $zipName
$sumPath = Join-Path $publishRoot $sumName

if (-not (Test-Path $zipPath) -or -not (Test-Path $sumPath)) {
    Write-Host "Portable ZIP not found, invoking Publish-Portable.ps1..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "Publish-Portable.ps1") -VersionLabel $VersionLabel -SkipTests
    if ($LASTEXITCODE -ne 0) { throw "Publish-Portable.ps1 failed." }
}

$kitRoot = Join-Path $publishRoot "sandbox-kit"
$inputDir = Join-Path $kitRoot "input"
$toolsDir = Join-Path $inputDir "tools"
$fixturesDir = Join-Path $inputDir "fixtures"
$outputDir = Join-Path $kitRoot "output"

if (Test-Path $inputDir) {
    Remove-Item $inputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $inputDir, $toolsDir, $fixturesDir, $outputDir -Force | Out-Null

Write-Host "=== 1. Publishing Self-Contained SyntheticCrashTool (Sandbox Isolated Only) ===" -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "tests\SyntheticCrashTool\SyntheticCrashTool.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $toolsDir
if ($LASTEXITCODE -ne 0) { throw "SyntheticCrashTool publish failed with exit code $LASTEXITCODE" }

Copy-Item $zipPath -Destination $inputDir -Force
Copy-Item $sumPath -Destination $inputDir -Force
Copy-Item (Join-Path $repoRoot "tests\Fixtures\EngineLogs\unity-player-gpu-lost.log") -Destination $fixturesDir -Force
Copy-Item (Join-Path $repoRoot "tests\Fixtures\EngineLogs\unreal-crashcontext-oom.xml") -Destination $fixturesDir -Force

Write-Host "=== 2. Generating Run-InSandbox.ps1 (PS 5.1 Compatible, UTF-8 BOM) ===" -ForegroundColor Cyan
$sandboxScriptTemplate = @'
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$testKitDir = "C:\Users\WDAGUtilityAccount\Desktop\GCK-TestKit"
$outputDir  = "C:\Users\WDAGUtilityAccount\Desktop\GCK-Output"
$appDir     = "C:\Users\WDAGUtilityAccount\Desktop\GameCrashKit-App"
$sampleDir  = "C:\Users\WDAGUtilityAccount\Desktop\SampleLogs"
$zipName    = "__ZIP_NAME__"
$sumName    = "__SUM_NAME__"

$report = New-Object System.Collections.Generic.List[string]
function Log-Line([string]$line) {
    Write-Host $line
    $report.Add($line)
}

Log-Line "============================================================"
Log-Line " GameCrash Kit 离线干净 Windows 沙盒自动化与交互验收台"
Log-Line "============================================================"

$cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
Log-Line ("[1/6] 系统环境: Build {0}.{1} | UICulture={2} | IsAdmin={3}" -f $cv.CurrentBuildNumber, $cv.UBR, [Globalization.CultureInfo]::CurrentUICulture.Name, $isAdmin)
Log-Line ("[1/6] 预装 .NET CLI: {0}" -f ($(if ($dotnetCmd) { "存在 (" + $dotnetCmd.Source + ")" } else { "未安装（符合干净环境要求）" })))

$offlineOk = $false
try {
    $null = [System.Net.Dns]::GetHostAddresses("example.com")
    Log-Line "[2/6] 网络隔离检查: 警告 - DNS 解析未失败，当前沙盒可能未断开网络。"
}
catch {
    $offlineOk = $true
    Log-Line "[2/6] 网络隔离检查: 已确认处于完全断网状态 (DNS 不可达)。"
}

$zipFile = Join-Path $testKitDir $zipName
$sumFile = Join-Path $testKitDir $sumName
$actualHash = (Get-FileHash $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedLine = (Get-Content $sumFile -TotalCount 1).Trim()
$expectedHash = ($expectedLine -split '\s+')[0].ToLowerInvariant()

if ($actualHash -eq $expectedHash) {
    Log-Line ("[3/6] 便携包 SHA-256 校验通过: {0}" -f $actualHash)
} else {
    Log-Line ("[3/6] 便携包 SHA-256 校验失败! 实际={0} 预期={1}" -f $actualHash, $expectedHash)
}

if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
Expand-Archive -Path $zipFile -DestinationPath $appDir -Force
Log-Line ("[3/6] 已解压绿色便携包至: {0}" -f $appDir)

New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null
$sampleLog = Join-Path $sampleDir "unity-player-gpu-lost.log"
Copy-Item (Join-Path $testKitDir "fixtures\unity-player-gpu-lost.log") -Destination $sampleLog -Force
Copy-Item (Join-Path $testKitDir "fixtures\unreal-crashcontext-oom.xml") -Destination (Join-Path $sampleDir "unreal-crashcontext-oom.xml") -Force
$preHash = (Get-FileHash $sampleLog -Algorithm SHA256).Hash
$preWrite = (Get-Item $sampleLog).LastWriteTimeUtc.ToString("o")
Log-Line ("[4/6] 准备只读样本日志 (前置 SHA-256={0})" -f $preHash)

Write-Host ""
Write-Host "[5/6] 正在沙盒内部安全触发 1 次受控 0xc0000005 崩溃样本 (SyntheticCrashTool)..." -ForegroundColor Yellow
$crashExe = Join-Path $testKitDir "tools\SyntheticCrashTool.exe"
$p = Start-Process -FilePath $crashExe -ArgumentList "--mode", "av", "--confirm-crash" -PassThru
$null = $p.WaitForExit(8000)
if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Seconds 3

$ev1000Count = 0
try {
    $since = [DateTime]::UtcNow.AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    $xpath = "*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[@SystemTime>='$since']]]"
    $q = New-Object System.Diagnostics.Eventing.Reader.EventLogQuery("Application", [System.Diagnostics.Eventing.Reader.PathType]::LogName, $xpath)
    $r = New-Object System.Diagnostics.Eventing.Reader.EventLogReader($q)
    while (($ev = $r.ReadEvent()) -ne $null) {
        $xml = $ev.ToXml()
        if ($xml -match "SyntheticCrashTool") { $ev1000Count++ }
        $ev.Dispose()
    }
    $r.Dispose()
} catch {
    Log-Line ("[5/6] 读取沙盒 Event 1000 异常: " + $_.Exception.Message)
}
Log-Line ("[5/6] 沙盒内已确认捕获真实 Event 1000 (SyntheticCrashTool, 0xc0000005) 条数: {0}" -f $ev1000Count)

Write-Host ""
Write-Host "[6/6] 正在启动 GameCrashKit.App.exe，请按下面 4 步实测：" -ForegroundColor Cyan
Write-Host "  1) 确认未弹出任何 UAC 管理员提权框，按 F5 扫描后，左侧应出现 SyntheticCrashTool.exe (0xc0000005)"
Write-Host "  2) 把桌面 SampleLogs 里的 unity-player-gpu-lost.log 拖入窗口，确认触发 ENGINE-GPU-LOST@1"
Write-Host "  3) 按 Ctrl+E 预览急诊单，分别保存 PNG 和 Markdown 到桌面 GCK-Output 文件夹"
Write-Host "  4) 关闭 GameCrash Kit 窗口后，回到本命令行按【回车键】完成只读哈希校验与报告保存"
Write-Host ""

Start-Process -FilePath (Join-Path $appDir "GameCrashKit.App.exe")
$null = Read-Host "完成上述界面实测并关闭软件后，请按回车键继续校验"

$postHash = (Get-FileHash $sampleLog -Algorithm SHA256).Hash
$postWrite = (Get-Item $sampleLog).LastWriteTimeUtc.ToString("o")
$hashMatch = ($preHash -eq $postHash) -and ($preWrite -eq $postWrite)
Log-Line ("[只读不变校验] 样本日志 SHA-256 与修改时间完全一致: {0} (Pre={1}, Post={2})" -f $hashMatch, $preHash, $postHash)

$exported = @(Get-ChildItem -Path $outputDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne "sandbox-report.txt" })
Log-Line ("[导出检查] GCK-Output 目录共检测到 {0} 个用户导出文件:" -f $exported.Count)
foreach ($ef in $exported) {
    Log-Line ("  - {0} ({1} bytes)" -f $ef.Name, $ef.Length)
}

$reportFile = Join-Path $outputDir "sandbox-report.txt"
$report | Set-Content -Path $reportFile -Encoding UTF8
Write-Host ""
Write-Host ("沙盒实测报告已回写至宿主机: publish\sandbox-kit\output\sandbox-report.txt") -ForegroundColor Green
'@

$sandboxScript = $sandboxScriptTemplate.Replace("__ZIP_NAME__", $zipName).Replace("__SUM_NAME__", $sumName)
$sandboxScriptPath = Join-Path $inputDir "Run-InSandbox.ps1"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($sandboxScriptPath, $sandboxScript, $utf8Bom)

Write-Host "=== 3. Generating GameCrashKit-Offline-Sandbox.wsb ===" -ForegroundColor Cyan
$wsbContent = @"
<Configuration>
  <VGpu>Enable</VGpu>
  <Networking>Disable</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$inputDir</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\GCK-TestKit</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$outputDir</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\GCK-Output</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoExit -ExecutionPolicy Bypass -File "C:\Users\WDAGUtilityAccount\Desktop\GCK-TestKit\Run-InSandbox.ps1"</Command>
  </LogonCommand>
</Configuration>
"@

$wsbPath = Join-Path $kitRoot "GameCrashKit-Offline-Sandbox.wsb"
[System.IO.File]::WriteAllText($wsbPath, $wsbContent, [System.Text.UTF8Encoding]::new($false))

Write-Host ""
Write-Host "Offline Sandbox Kit Ready:" -ForegroundColor Green
Write-Host "  Double-click to launch : $wsbPath"
Write-Host "  Exported outputs dir   : $outputDir"
