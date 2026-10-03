[CmdletBinding(SupportsShouldProcess)]
param([string]$GameDir = $env:STS2_GAME_DIR)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'Pass -GameDir or set STS2_GAME_DIR.' }
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $game 'SlayTheSpire2.exe'))) { throw 'Game executable not found.' }
$source = Join-Path $root 'dist/SpireAiCoach'
if (-not (Test-Path -LiteralPath (Join-Path $source 'SpireAiCoach.dll'))) { throw 'Run Build.ps1 first.' }
$gameExe = (Resolve-Path -LiteralPath (Join-Path $game 'SlayTheSpire2.exe')).Path
$running = @(Get-CimInstance Win32_Process -Filter "Name='SlayTheSpire2.exe'")
foreach ($process in $running) {
    if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) { throw 'Cannot identify a running game process; exit it before installing.' }
    $processExe = (Resolve-Path -LiteralPath $process.ExecutablePath).Path
    if ([string]::Equals($processExe, $gameExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Exit the game before installing. The installer will not stop it.'
    }
}
$destination = Join-Path $game 'mods/SpireAiCoach'
if ($PSCmdlet.ShouldProcess($destination, 'Install Spire AI Coach')) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($name in @('SpireAiCoach.dll', 'SpireAiCoach.json')) {
        $target = Join-Path $destination $name
        if (Test-Path -LiteralPath $target) {
            $backup = $target + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.bak'
            Copy-Item -LiteralPath $target -Destination $backup
        }
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target -Force
    }
    Write-Output "Installed to $destination. Restart the game and enable the mod."
    # Other chats can be testing isolated copies. Install only into this game;
    # defer optional cache cleanup while any such process remains in use.
    if ($running.Count -eq 0) { & (Join-Path $PSScriptRoot 'RemoveLegacyWorkerCopies.ps1') }
}
