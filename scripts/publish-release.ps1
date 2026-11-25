param(
    [string]$TargetDir = "",
    [string]$LauncherExe = "",
    [string]$RepoRoot = "",
    [switch]$CreateZip
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    & dotnet build (Join-Path $RepoRoot "src\PopTranslate.App\PopTranslate.App.csproj") -c Release -p:Platform=x64 -warnaserror
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    exit 0
}

$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path $TargetDir)) {
    throw "TargetDir does not exist: $TargetDir"
}

# 1. Prune compile-time metadata (.winmd), debug symbols (.pdb), XML docs, and crash dumper from Release output
Get-ChildItem -Path $TargetDir -Filter "*.winmd" -File -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $TargetDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $TargetDir -Filter "*.xml" -File -ErrorAction SilentlyContinue | Remove-Item -Force
$createDump = Join-Path $TargetDir "createdump.exe"
if (Test-Path $createDump) { Remove-Item $createDump -Force }

# 2. Prune 80+ unused satellite locale directories (keep only Chinese & English + functional app directories)
$keepDirs = @(
    "Assets",
    "Controls",
    "Pages",
    "Windows",
    "Microsoft.UI.Xaml",
    "runtimes",
    "zh-CN",
    "zh-Hans",
    "zh-TW",
    "en-US",
    "en-us"
)

Get-ChildItem -Path $TargetDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    if ($keepDirs -notcontains $_.Name) {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}

# 3. Assemble clean Release distribution at <RepoRoot>/release/PopTranslate-win-x64/
# IMPORTANT: Only replace runtime/ and root binaries/docs — NEVER delete portable markers or data/ user config!
$distRoot = Join-Path $RepoRoot "release\PopTranslate-win-x64"
$distRuntime = Join-Path $distRoot "runtime"

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
if (Test-Path $distRuntime) {
    Remove-Item -LiteralPath $distRuntime -Recurse -Force
}
New-Item -ItemType Directory -Path $distRuntime -Force | Out-Null

# Copy pruned self-contained runtime into runtime/ subfolder
Copy-Item -Path (Join-Path $TargetDir "*") -Destination $distRuntime -Recurse -Force

# Copy clean root PopTranslate.exe launcher + docs
$launcherCandidates = @(
    $LauncherExe,
    (Join-Path $RepoRoot "src\PopTranslate.Launcher\bin\x64\Release\PopTranslate.exe"),
    (Join-Path $RepoRoot "src\PopTranslate.Launcher\bin\Release\PopTranslate.exe")
)
$resolvedLauncher = $launcherCandidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) } | Select-Object -First 1
if (-not $resolvedLauncher) {
    throw "PopTranslate.exe launcher was not found in expected Release output paths."
}
Copy-Item -LiteralPath $resolvedLauncher -Destination (Join-Path $distRoot "PopTranslate.exe") -Force

foreach ($doc in @("README.md", "LICENSE")) {
    $docPath = Join-Path $RepoRoot $doc
    if (Test-Path $docPath) {
        Copy-Item -LiteralPath $docPath -Destination (Join-Path $distRoot $doc) -Force
    }
}

if ($CreateZip) {
    $zipPath = Join-Path $RepoRoot "release\PopTranslate-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $zipItems = @(
        (Join-Path $distRoot "PopTranslate.exe"),
        (Join-Path $distRoot "README.md"),
        (Join-Path $distRoot "LICENSE"),
        $distRuntime
    ) | Where-Object { Test-Path $_ }
    Compress-Archive -Path $zipItems -DestinationPath $zipPath -CompressionLevel Optimal
}
