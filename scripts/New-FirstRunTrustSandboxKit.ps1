[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.2.0-alpha.3",
    [switch]$StageLocalZipFallback,
    [string]$SourceZipPath = "",
    [string]$SourceChecksumPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path $repoRoot "publish"
$kitRoot = Join-Path $publishRoot "trust-sandbox"
$inputDir = Join-Path $kitRoot "input"
$outputDir = Join-Path $kitRoot "output"

if (Test-Path $inputDir) {
    Remove-Item $inputDir -Recurse -Force
}
if (Test-Path $outputDir) {
    Remove-Item (Join-Path $outputDir "*") -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $inputDir, $outputDir -Force | Out-Null

Copy-Item -Path (Join-Path $PSScriptRoot "Test-FirstRunTrust.ps1") -Destination (Join-Path $inputDir "Test-FirstRunTrust.ps1") -Force

$zipName = "GameCrashKit-$VersionLabel-win-x64.zip"
$sumName = "GameCrashKit-$VersionLabel-win-x64.SHA256SUMS.txt"
$localZip = if (-not [string]::IsNullOrWhiteSpace($SourceZipPath)) { $SourceZipPath } else { Join-Path $publishRoot $zipName }
$localSum = if (-not [string]::IsNullOrWhiteSpace($SourceChecksumPath)) { $SourceChecksumPath } else { Join-Path $publishRoot $sumName }
$fallbackArgs = ""

if ($StageLocalZipFallback -and (Test-Path $localZip)) {
    Copy-Item -Path $localZip -Destination (Join-Path $inputDir $zipName) -Force
    if (-not [string]::IsNullOrWhiteSpace($localSum) -and (Test-Path $localSum)) {
        Copy-Item -Path $localSum -Destination (Join-Path $inputDir $sumName) -Force
        $fallbackArgs = "-LocalZipPath `"`$testKitDir\$zipName`" -LocalChecksumPath `"`$testKitDir\$sumName`""
    }
    else {
        $fallbackArgs = "-LocalZipPath `"`$testKitDir\$zipName`""
    }
}

$runnerTemplate = @'
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$testKitDir = "C:\Users\WDAGUtilityAccount\Desktop\GCK-TrustKit"
$outputDir  = "C:\Users\WDAGUtilityAccount\Desktop\GCK-TrustOutput"
$scriptPath = Join-Path $testKitDir "Test-FirstRunTrust.ps1"

& $scriptPath -VersionLabel "__VERSION_LABEL__" -OutputDir $outputDir __FALLBACK_ARGS__
exit $LASTEXITCODE
'@

$runnerScript = $runnerTemplate.
    Replace("__VERSION_LABEL__", $VersionLabel).
    Replace("__FALLBACK_ARGS__", $fallbackArgs)
$runnerPath = Join-Path $inputDir "Run-TrustInSandbox.ps1"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($runnerPath, $runnerScript, $utf8Bom)

$wsbContent = @"
<Configuration>
  <VGpu>Enable</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>6144</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$inputDir</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\GCK-TrustKit</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$outputDir</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\GCK-TrustOutput</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoExit -ExecutionPolicy Bypass -File "C:\Users\WDAGUtilityAccount\Desktop\GCK-TrustKit\Run-TrustInSandbox.ps1"</Command>
  </LogonCommand>
</Configuration>
"@

$wsbPath = Join-Path $kitRoot "GameCrashKit-Trust-Sandbox.wsb"
[System.IO.File]::WriteAllText($wsbPath, $wsbContent, [System.Text.UTF8Encoding]::new($false))

Write-Host "First-Run Trust Sandbox Kit Ready:" -ForegroundColor Green
Write-Host "  WSB    : $wsbPath"
Write-Host "  Output : $outputDir"
