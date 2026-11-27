[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.2.0-alpha.2"
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
$verifierDir = Join-Path $inputDir "verifier"
$fixturesDir = Join-Path $inputDir "fixtures"
$outputDir = Join-Path $kitRoot "output"

if (Test-Path $inputDir) {
    Remove-Item $inputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $inputDir, $toolsDir, $verifierDir, $fixturesDir, $outputDir -Force | Out-Null

Write-Host "=== 1a. Publishing Self-Contained SyntheticCrashTool (Sandbox Isolated Only) ===" -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "tests\SyntheticCrashTool\SyntheticCrashTool.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $toolsDir
if ($LASTEXITCODE -ne 0) { throw "SyntheticCrashTool publish failed with exit code $LASTEXITCODE" }

Write-Host "=== 1b. Publishing Self-Contained SandboxAutoVerifier (Sandbox Isolated Only) ===" -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "tests\SandboxAutoVerifier\SandboxAutoVerifier.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o $verifierDir
if ($LASTEXITCODE -ne 0) { throw "SandboxAutoVerifier publish failed with exit code $LASTEXITCODE" }

Copy-Item $zipPath -Destination $inputDir -Force
Copy-Item $sumPath -Destination $inputDir -Force
Copy-Item (Join-Path $repoRoot "tests\Fixtures\EngineLogs\unity-player-gpu-lost.log") -Destination $fixturesDir -Force
Copy-Item (Join-Path $repoRoot "tests\Fixtures\EngineLogs\unreal-crashcontext-oom.xml") -Destination $fixturesDir -Force

Write-Host "=== 2. Generating Run-InSandbox.ps1 (Unattended PS 5.1 Compatible, UTF-8 BOM) ===" -ForegroundColor Cyan
$sandboxScriptTemplate = @'
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$testKitDir  = "C:\Users\WDAGUtilityAccount\Desktop\GCK-TestKit"
$outputDir   = "C:\Users\WDAGUtilityAccount\Desktop\GCK-Output"
$appDir      = "C:\Users\WDAGUtilityAccount\Desktop\GameCrashKit-App"
$sampleDir   = "C:\Users\WDAGUtilityAccount\Desktop\SampleLogs"
$zipName     = "__ZIP_NAME__"
$sumName     = "__SUM_NAME__"
$expectedVer = "__EXPECTED_VERSION__"

$bootstrapLog = New-Object System.Collections.Generic.List[string]
function Log-Boot([string]$line) {
    Write-Host $line
    $bootstrapLog.Add($line)
}

function Fail-Bootstrap([string]$reason) {
    Log-Boot ("[FAIL] " + $reason)
    $bootstrapLog | Set-Content -Path (Join-Path $outputDir "sandbox-report.txt") -Encoding UTF8
    ("FAIL " + [DateTimeOffset]::Now.ToString("yyyy-MM-dd HH:mm:ss zzz")) | Set-Content -Path (Join-Path $outputDir "sandbox-done.flag") -Encoding ASCII
    Write-Host $reason -ForegroundColor Red
    exit 1
}

Log-Boot "============================================================"
Log-Boot " GameCrash Kit 全自动无人值守离线 Windows 沙盒验收控制台"
Log-Boot "============================================================"

$zipFile = Join-Path $testKitDir $zipName
$sumFile = Join-Path $testKitDir $sumName
$actualHash = (Get-FileHash $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedLine = (Get-Content $sumFile -TotalCount 1).Trim()
$expectedHash = ($expectedLine -split '\s+')[0].ToLowerInvariant()

if ($actualHash -ne $expectedHash) {
    Fail-Bootstrap ("便携包 SHA-256 校验失败! 实际={0} 预期={1}" -f $actualHash, $expectedHash)
}
Log-Boot ("[1/4] 便携包 SHA-256 校验通过: {0}" -f $actualHash)

if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
Expand-Archive -Path $zipFile -DestinationPath $appDir -Force
Log-Boot ("[2/4] 已解压绿色便携包至: {0}" -f $appDir)

New-Item -ItemType Directory -Path $sampleDir -Force | Out-Null
$sampleLog = Join-Path $sampleDir "unity-player-gpu-lost.log"
Copy-Item (Join-Path $testKitDir "fixtures\unity-player-gpu-lost.log") -Destination $sampleLog -Force
Copy-Item (Join-Path $testKitDir "fixtures\unreal-crashcontext-oom.xml") -Destination (Join-Path $sampleDir "unreal-crashcontext-oom.xml") -Force
Log-Boot ("[2/4] 已就绪只读测试日志: {0}" -f $sampleLog)

Write-Host "[3/4] 正在触发 1 次受控 0xc0000005 崩溃样本 (SyntheticCrashTool)..." -ForegroundColor Yellow
$crashExe = Join-Path $testKitDir "tools\SyntheticCrashTool.exe"
$p = Start-Process -FilePath $crashExe -ArgumentList "--mode", "av", "--confirm-crash" -PassThru
$null = $p.WaitForExit(8000)
if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Seconds 3

$verifierExe = Join-Path $testKitDir "verifier\SandboxAutoVerifier.exe"
if (-not (Test-Path $verifierExe)) {
    Fail-Bootstrap ("未找到无人值守校验器: {0}" -f $verifierExe)
}

Write-Host "[4/4] 正在运行全自动无人值守校验套件 SandboxAutoVerifier.exe ..." -ForegroundColor Cyan
& $verifierExe `
    --app-dir $appDir `
    --sample-log $sampleLog `
    --output-dir $outputDir `
    --expected-version $expectedVer
$verifierExit = $LASTEXITCODE

Write-Host ""
if ($verifierExit -eq 0) {
    Write-Host "[DONE] 沙盒全自动无人值守验收全部通过 (ExitCode=0)，报告与产物已写入宿主机 GCK-Output。" -ForegroundColor Green
} else {
    Write-Host ("[FAIL] 沙盒全自动验收存在失败项 (ExitCode={0})，请检查 sandbox-report.txt。" -f $verifierExit) -ForegroundColor Red
}
'@

$expectedVer = $VersionLabel.TrimStart('v')
$sandboxScript = $sandboxScriptTemplate.
    Replace("__ZIP_NAME__", $zipName).
    Replace("__SUM_NAME__", $sumName).
    Replace("__EXPECTED_VERSION__", $expectedVer)
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
