param([string]$GameDir = $env:STS2_GAME_DIR)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'Pass -GameDir or set STS2_GAME_DIR.' }
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $GameDir 'data_sts2_windows_x86_64'
if (-not (Test-Path -LiteralPath (Join-Path $data 'sts2.dll'))) { throw 'Game assembly not found.' }
Push-Location $root
try {
    dotnet run --project tests/SpireAiCoach.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet build src/SpireAiCoach.Mod/SpireAiCoach.Mod.csproj -c Release "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
    $package = Join-Path $root 'dist/SpireAiCoach'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    Copy-Item -LiteralPath 'src/SpireAiCoach.Mod/bin/Release/net9.0/SpireAiCoach.dll' -Destination $package
    Copy-Item -LiteralPath 'SpireAiCoach.json' -Destination $package
    Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $package 'README.md')
    Compress-Archive -LiteralPath $package -DestinationPath (Join-Path $root 'dist/SpireAiCoach-0.1.0.zip') -Force
    $zipPath = Join-Path $root 'dist/SpireAiCoach-0.1.0.zip'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($zipPath))).Replace('-', '').ToLowerInvariant()
        Write-Output "Package: $zipPath"
        Write-Output "SHA256: $digest"
    } finally { $sha.Dispose() }
} finally { Pop-Location }
