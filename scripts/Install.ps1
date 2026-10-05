[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$GameDir = $env:STS2_GAME_DIR,
    [string]$ExpectedStatePath,
    [string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force -DisableNameChecking
$game = Resolve-CoachGameDir $root $GameDir
$destination = Get-CoachInstallPath $game
$package = Get-CoachPackageState $root
$source = Join-Path $root 'dist/SpireAiCoach'
if (-not $PSCmdlet.ShouldProcess($destination, 'Install Spire AI Coach')) { return }
$lock = Enter-CoachReleaseLock $destination
$staged = [ordered]@{}
$backups = [ordered]@{}
$replaced = @()
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
try {
    $before = Get-CoachInstalledState $destination
    if ($ExpectedStatePath) {
        $expected = Read-CoachJson $ExpectedStatePath
        if (-not $expected) { throw 'Installation baseline is missing.' }
        Assert-CoachInstalledState $destination $expected
    }
    if ($before.version -and ([Version]$package.version -lt [Version]$before.version)) { throw 'Refusing to overwrite a newer installed version.' }
    $hashes = [ordered]@{ 'SpireAiCoach.dll' = $package.dll_sha256; 'SpireAiCoach.json' = $package.manifest_sha256 }
    $identical = $before.files.'SpireAiCoach.dll' -eq $package.dll_sha256 -and $before.files.'SpireAiCoach.json' -eq $package.manifest_sha256
    if ($identical) { Write-Host "Already installed: $($package.version). No files need replacing." }
    else {
        if ($before.version -eq $package.version -and -not $ExpectedStatePath) {
            throw 'Same version has different installed files. Use Update.ps1 to capture and check the installation baseline.'
        }
        Assert-CoachGameExited $game
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($name in $hashes.Keys) {
            $target = Join-Path $destination $name
            $staged[$name] = "$target.$stamp.tmp"
            Copy-Item -LiteralPath (Join-Path $source $name) -Destination $staged[$name]
            if ((Get-CoachHash $staged[$name]) -ne $hashes[$name]) { throw "Staged package changed: $name" }
            if (Test-Path -LiteralPath $target) {
                $backups[$name] = "$target.$stamp.bak"
                Copy-Item -LiteralPath $target -Destination $backups[$name]
                if ((Get-CoachHash $backups[$name]) -ne $before.files.$name) { throw "Installation changed before backup: $name" }
            }
        }
        Assert-CoachGameExited $game
        Assert-CoachInstalledState $destination $before
        foreach ($name in $hashes.Keys) {
            $target = Join-Path $destination $name
            # Replace the directory entry instead of changing a potentially hard-linked worker DLL.
            if (Test-Path -LiteralPath $target) { [IO.File]::Replace($staged[$name], $target, [NullString]::Value) }
            else { [IO.File]::Move($staged[$name], $target) }
            $replaced += $name
        }
        $after = Get-CoachInstalledState $destination
        foreach ($name in $hashes.Keys) {
            if ($after.files.$name -ne $hashes[$name]) { throw "Installed hash mismatch: $name" }
        }
        Write-Host "Installed $($package.version) to $destination. Restart the game to load it."
    }
    if (-not $ReceiptPath) { $ReceiptPath = Join-Path $root "work/update-local/install-$stamp.json" }
    Write-CoachJson $ReceiptPath ([ordered]@{
        status = $(if ($identical) { 'already-installed' } else { 'installed' })
        version = $package.version
        dll_sha256 = $package.dll_sha256
        manifest_sha256 = $package.manifest_sha256
        previous = $before
        backups = $backups
        cache_cleanup = $false
        started_player = $false
        utc = [DateTime]::UtcNow.ToString('o')
    })
} catch {
    # A partial two-file update must not leave a new DLL with the old manifest.
    foreach ($name in $replaced) {
        $target = Join-Path $destination $name
        if ((Get-CoachHash $target) -ne $hashes[$name]) { throw "Another writer changed $target; rollback was not attempted." }
        if ($backups.Contains($name)) {
            Copy-Item -LiteralPath $backups[$name] -Destination $staged[$name] -Force
            [IO.File]::Replace($staged[$name], $target, [NullString]::Value)
        } else { Remove-Item -LiteralPath $target -Force }
    }
    throw
} finally {
    foreach ($path in $staged.Values) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $lock.ReleaseMutex()
    $lock.Dispose()
}
