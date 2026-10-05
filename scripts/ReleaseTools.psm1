$ErrorActionPreference = 'Stop'

function Get-CoachHash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-CoachFingerprint([string]$Root, [string[]]$Scopes, [string[]]$Values = @()) {
    $files = foreach ($scope in $Scopes) {
        $path = Join-Path $Root $scope
        if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path }
        elseif (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' -and
                $_.Extension -in @('.cs', '.csproj', '.props', '.targets', '.json', '.txt')
            }
        }
    }
    $entries = @($Values) + @($files | Sort-Object FullName -Unique | ForEach-Object {
        $_.FullName.Substring($Root.Length).Replace('\', '/') + ':' + (Get-CoachHash $_.FullName)
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n")))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Read-CoachJson([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    }
}

function Write-CoachJson([string]$Path, $Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, (($Value | ConvertTo-Json -Depth 20) + "`n"), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Resolve-CoachGameDir([string]$Root, [string]$GameDir) {
    if ([string]::IsNullOrWhiteSpace($GameDir)) {
        $previous = Read-CoachJson (Join-Path $Root 'work/update-local/last-target.json')
        if ($previous) { $GameDir = $previous.game_dir }
    }
    if ([string]::IsNullOrWhiteSpace($GameDir) -and (Test-Path -LiteralPath (Join-Path $Root 'GamePath.props'))) {
        [xml]$settings = [IO.File]::ReadAllText((Join-Path $Root 'GamePath.props'))
        $GameDir = @($settings.Project.PropertyGroup.GameDir | Where-Object { $_ }) | Select-Object -First 1
    }
    if ([string]::IsNullOrWhiteSpace($GameDir)) { throw 'First run: pass -GameDir or set STS2_GAME_DIR.' }
    $game = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GameDir).Path).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $game 'SlayTheSpire2.exe') -PathType Leaf)) { throw 'Game executable not found.' }
    return $game
}

function Get-CoachInstallPath([string]$GameDir) {
    $destination = [IO.Path]::GetFullPath((Join-Path $GameDir 'mods/SpireAiCoach'))
    if (-not $destination.StartsWith($GameDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installation must remain inside the selected game directory.'
    }
    foreach ($path in @((Join-Path $GameDir 'mods'), $destination)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing redirected installation directory: $path"
        }
    }
    return $destination
}

function Get-CoachInstalledState([string]$Destination) {
    $files = [ordered]@{}
    foreach ($name in @('SpireAiCoach.dll', 'SpireAiCoach.json')) {
        $path = Join-Path $Destination $name
        $files[$name] = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-CoachHash $path } else { $null }
    }
    $manifest = Read-CoachJson (Join-Path $Destination 'SpireAiCoach.json')
    return [ordered]@{ version = $(if ($manifest) { $manifest.version } else { $null }); files = $files }
}

function Assert-CoachInstalledState([string]$Destination, $Expected) {
    $actual = Get-CoachInstalledState $Destination
    foreach ($name in @('SpireAiCoach.dll', 'SpireAiCoach.json')) {
        if ($actual.files.$name -ne $Expected.files.$name) {
            throw 'Installed files changed during this update. Re-run from the current installation; nothing has been overwritten.'
        }
    }
}

function Get-CoachPackageState([string]$Root) {
    $package = Join-Path $Root 'dist/SpireAiCoach'
    $manifest = Read-CoachJson (Join-Path $package 'SpireAiCoach.json')
    if (-not $manifest -or $manifest.id -ne 'SpireAiCoach' -or -not ($manifest.version -as [Version])) {
        throw 'Missing or invalid package manifest. Run Update.ps1 first.'
    }
    $files = [ordered]@{}
    foreach ($name in @('SpireAiCoach.dll', 'SpireAiCoach.json')) { $files[$name] = Get-CoachHash (Join-Path $package $name) }
    $archivePath = Join-Path $Root "dist/SpireAiCoach-$($manifest.version).zip"
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'Package archive is missing. Run Update.ps1 to finish packaging.' }
    if (Test-Path -LiteralPath $archivePath) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($name in @('SpireAiCoach.dll', 'SpireAiCoach.json')) {
                $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq "SpireAiCoach/$name" })
                if ($entries.Count -ne 1) { throw "Invalid package archive entry: $name" }
                $stream = $entries[0].Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
                finally { $stream.Dispose(); $sha.Dispose() }
                if ($digest -ne $files[$name]) { throw "Package/archive mismatch: $name" }
            }
        } finally { $archive.Dispose() }
    }
    return [ordered]@{
        version = $manifest.version
        dll_sha256 = $files['SpireAiCoach.dll']
        manifest_sha256 = $files['SpireAiCoach.json']
        zip_sha256 = $(if (Test-Path -LiteralPath $archivePath) { Get-CoachHash $archivePath } else { $null })
    }
}

function Assert-CoachGameExited([string]$GameDir) {
    $gameExe = [IO.Path]::GetFullPath((Join-Path $GameDir 'SlayTheSpire2.exe'))
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SlayTheSpire2.exe'")) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) { throw 'Cannot identify a game process; exit the game before installing.' }
        if ([string]::Equals([IO.Path]::GetFullPath($process.ExecutablePath), $gameExe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Exit the game, then re-run Update.ps1. Passed validation and the prepared package are retained.'
        }
    }
}

function Enter-CoachReleaseLock([string]$Name) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Name.ToLowerInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $mutex = New-Object Threading.Mutex($false, "Local\SpireAiCoach-Release-$key")
    try { $held = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { $mutex.Dispose(); throw 'Another update is using this workspace or installation. Wait for it to finish.' }
    return $mutex
}

Export-ModuleMember -Function *-Coach*
