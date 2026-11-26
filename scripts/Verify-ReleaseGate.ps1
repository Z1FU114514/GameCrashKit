[CmdletBinding()]
param(
    [string]$PortableDir = "",
    [int]$BenchmarkRuns = 20
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PortableDir)) {
    $PortableDir = Join-Path $repoRoot "publish\GameCrashKit-v0.1.0-alpha.1-win-x64"
} elseif (-not [System.IO.Path]::IsPathRooted($PortableDir)) {
    $PortableDir = (Resolve-Path (Join-Path $repoRoot $PortableDir)).Path
}

Write-Host "=== GameCrash Kit M4 Automated Release-Gate Verification ===" -ForegroundColor Cyan

# 1. Host / VM Environment Summary
$cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$buildNum = [int]$cv.CurrentBuildNumber
$osFamily = if ($buildNum -ge 22000) { "Windows 11" } elseif ($buildNum -ge 10240) { "Windows 10" } else { "Windows" }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$uiCulture = [Globalization.CultureInfo]::CurrentUICulture.Name
Write-Host ("Environment : {0} {1} (Build {2}.{3}) | UICulture={4} | IsAdmin={5}" -f $osFamily, $cv.DisplayVersion, $buildNum, $cv.UBR, $uiCulture, $isAdmin)

# 2. Manifest & Portable Package Hygiene
$manifestPath = Join-Path $repoRoot "src\GameCrashKit.App\app.manifest"
if (Test-Path $manifestPath) {
    $manifestRaw = Get-Content $manifestPath -Raw
    if ($manifestRaw -notmatch 'level="asInvoker"') {
        throw "FAIL: app.manifest is not locked to asInvoker!"
    }
    Write-Host "Manifest    : PASS (requestedExecutionLevel=asInvoker)" -ForegroundColor Green
}

if (-not (Test-Path $PortableDir)) {
    throw "FAIL: Portable directory not found: $PortableDir"
}

$requiredPortableFiles = @("GameCrashKit.App.exe", "LICENSE-MIT", "LICENSE-APACHE", "README.md", "PRIVACY.md", "SUPPORT_MATRIX.md")
foreach ($req in $requiredPortableFiles) {
    if (-not (Test-Path (Join-Path $PortableDir $req))) {
        throw "FAIL: Missing required portable package file: $req"
    }
}
$forbiddenLeak = Get-ChildItem -Path $PortableDir -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "*SyntheticCrashTool*" -or $_.Name -like "*SandboxAutoVerifier*" }
if ($forbiddenLeak) {
    throw "FAIL: Dev/test binary found inside portable package: $($forbiddenLeak.Name -join ', ')"
}
Write-Host "Portable    : PASS (all required binaries/docs present, 0 dev tools leaked)" -ForegroundColor Green

# 3 & 4. Read-Only Invariance Check & 20-Run Scan Latency Benchmark (AC-05: P95 <= 5000 ms)
$coreDll = Join-Path $PortableDir "GameCrashKit.Core.dll"
$winDll = Join-Path $PortableDir "GameCrashKit.Windows.dll"
if (-not (Test-Path $coreDll)) {
    $coreDll = Join-Path $repoRoot "src\GameCrashKit.Core\bin\Release\net10.0\GameCrashKit.Core.dll"
    $winDll = Join-Path $repoRoot "src\GameCrashKit.Windows\bin\Release\net10.0-windows\GameCrashKit.Windows.dll"
}

$canLoadNet10InProcess = $false
try {
    if ([System.Environment]::Version.Major -ge 10) {
        Add-Type -Path $coreDll
        Add-Type -Path $winDll
        $canLoadNet10InProcess = $true
    }
}
catch {
    $canLoadNet10InProcess = $false
}

if ($canLoadNet10InProcess) {
    $tempFile = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($tempFile, "Initialize engine version: 2022.3.20f1c1`nD3D11: DXGI_ERROR_DEVICE_REMOVED`n")
        $h1 = (Get-FileHash $tempFile -Algorithm SHA256).Hash
        $readRes = [GameCrashKit.Windows.FileSystem.SafeLocalFileReader]::ReadTailUtf8($tempFile, $null, 2097152)
        $h2 = (Get-FileHash $tempFile -Algorithm SHA256).Hash
        if (-not $readRes.Success -or $h1 -ne $h2) {
            throw "FAIL: Read-only SHA-256 invariance check failed!"
        }
        Write-Host "Read-Only   : PASS (SHA-256 unchanged after read: $h1)" -ForegroundColor Green
    }
    finally {
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
    }

    $coordinator = New-Object GameCrashKit.Windows.Scanning.WindowsScanCoordinator
    $latencies = New-Object System.Collections.Generic.List[double]
    for ($i = 0; $i -lt $BenchmarkRuns; $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $session = $coordinator.ScanAsync([GameCrashKit.Core.Models.ScanWindowPreset]::Last2Hours).GetAwaiter().GetResult()
        $sw.Stop()
        if ($session.IsBusy -or $session.WasCancelled) {
            throw "FAIL: Unexpected scan state during benchmark run $i"
        }
        $latencies.Add($sw.Elapsed.TotalMilliseconds)
    }

    $sorted = $latencies | Sort-Object
    $minMs = $sorted[0]
    $p50Idx = [Math]::Max(0, [int][Math]::Ceiling(0.50 * $BenchmarkRuns) - 1)
    $p95Idx = [Math]::Max(0, [int][Math]::Ceiling(0.95 * $BenchmarkRuns) - 1)
    $p50Ms = $sorted[$p50Idx]
    $p95Ms = $sorted[$p95Idx]
    $maxMs = $sorted[$sorted.Count - 1]

    if ($p95Ms -gt 5000.0) {
        throw ("FAIL: Default 2h scan P95={0:F1}ms exceeded 5000ms threshold!" -f $p95Ms)
    }

    Write-Host ("Benchmark   : PASS ({0} runs) Min={1:F1}ms | P50={2:F1}ms | P95={3:F1}ms | Max={4:F1}ms (Gate <= 5000ms)" -f $BenchmarkRuns, $minMs, $p50Ms, $p95Ms, $maxMs) -ForegroundColor Green
}
else {
    Write-Host ("Host pwsh runtime is .NET {0}; delegating Read-Only & Benchmark checks to .NET 10 test runner..." -f [System.Environment]::Version.ToString()) -ForegroundColor Yellow
    Push-Location $repoRoot
    try {
        dotnet test "GameCrashKit.sln" -c Release --no-build --filter "FullyQualifiedName~SourceLogFile_HashAndTimestampUnchangedAfterRead|FullyQualifiedName~WindowsScanCoordinator_ScanLast2HoursAndBuildReport"
        if ($LASTEXITCODE -ne 0) {
            throw "FAIL: Delegated .NET 10 Read-Only / Benchmark verification failed!"
        }
        Write-Host "Read-Only   : PASS (verified via .NET 10 test host)" -ForegroundColor Green
        Write-Host "Benchmark   : PASS (verified < 5000ms via .NET 10 test host)" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
Write-Host ""
Write-Host "ALL AUTOMATED M4 RELEASE GATES PASSED." -ForegroundColor Cyan
