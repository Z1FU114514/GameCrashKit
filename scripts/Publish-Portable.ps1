[CmdletBinding()]
param(
    [string]$VersionLabel = "v0.2.0-alpha.2",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$semver = $VersionLabel.TrimStart('v')
if ([string]::IsNullOrWhiteSpace($semver)) {
    throw "Invalid VersionLabel '$VersionLabel': resolved semver is empty."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    Write-Host "=== 1. Building GameCrashKit.sln (Release, Version=$semver) ===" -ForegroundColor Cyan
    dotnet build "GameCrashKit.sln" -c Release -p:Version=$semver
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    if (-not $SkipTests) {
        Write-Host "=== 2. Running Full Test Suite ===" -ForegroundColor Cyan
        dotnet test "GameCrashKit.sln" -c Release --no-build --verbosity normal
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
    }

    $publishRoot = Join-Path $repoRoot "publish"
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

    $folderName = "GameCrashKit-$VersionLabel-win-x64"
    $publishDir = Join-Path $publishRoot $folderName
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    Write-Host "=== 3. Publishing Self-Contained Win-x64 Portable Package (Version=$semver) ===" -ForegroundColor Cyan
    dotnet publish "src\GameCrashKit.App\GameCrashKit.App.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishTrimmed=false `
        -p:Version=$semver `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $appExePath = Join-Path $publishDir "GameCrashKit.App.exe"
    $productVer = (Get-Item $appExePath).VersionInfo.ProductVersion
    if (-not $productVer -or -not $productVer.StartsWith($semver, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "FATAL: Published GameCrashKit.App.exe ProductVersion '$productVer' does not start with expected '$semver'"
    }

    # Ensure dev/test tools never leak into user release packages
    $leakedTools = Get-ChildItem -Path $publishDir -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*SyntheticCrashTool*" -or $_.Name -like "*SandboxAutoVerifier*" }
    if ($leakedTools) {
        throw "FATAL: Dev/test binary leaked into publish directory: $($leakedTools.Name -join ', ')"
    }

    $docFiles = @(
        "LICENSE-MIT",
        "LICENSE-APACHE",
        "README.md",
        "docs\PRIVACY.md",
        "docs\SUPPORT_MATRIX.md"
    )
    foreach ($f in $docFiles) {
        Copy-Item -Path (Join-Path $repoRoot $f) -Destination $publishDir -Force
    }

    $zipName = "$folderName.zip"
    $zipPath = Join-Path $publishRoot $zipName
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Write-Host "=== 4. Compressing Portable ZIP & Generating SHA-256 Checksum ===" -ForegroundColor Cyan
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    $sha256 = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumFile = Join-Path $publishRoot "$folderName.SHA256SUMS.txt"
    "$sha256  $zipName" | Set-Content -Path $sumFile -Encoding ascii

    $sizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Portable Build Ready:" -ForegroundColor Green
    Write-Host "  Directory : $publishDir"
    Write-Host "  ZIP       : $zipPath ($sizeMb MB)"
    Write-Host "  SHA-256   : $sha256"
    Write-Host "  Checksums : $sumFile"
}
finally {
    Pop-Location
}
