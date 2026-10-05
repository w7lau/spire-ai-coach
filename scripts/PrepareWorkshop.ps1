[CmdletBinding()]
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = Join-Path $root 'dist/Workshop' }
$package = Join-Path $root 'dist/SpireAiCoach'
$manifest = Get-Content -LiteralPath (Join-Path $package 'SpireAiCoach.json') -Raw | ConvertFrom-Json
$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'SpireAiCoach.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $sourceManifest.version) { throw 'Package is stale; run Build.ps1.' }
$image = Join-Path $root 'workshop/image.png'
if (-not (Test-Path -LiteralPath $image) -or (Get-Item -LiteralPath $image).Length -ge 1MB) { throw 'Workshop image must exist and be smaller than 1 MB.' }
$content = Join-Path $Workspace 'content'
New-Item -ItemType Directory -Path $content -Force | Out-Null
$allowed = @('SpireAiCoach.dll', 'SpireAiCoach.json')
if (@(Get-ChildItem -LiteralPath $content -Force | Where-Object { $_.Name -notin $allowed -or $_.PSIsContainer }).Count -gt 0) {
    throw 'Unexpected Workshop content; use a clean workspace.'
}
foreach ($name in $allowed) { Copy-Item -LiteralPath (Join-Path $package $name) -Destination (Join-Path $content $name) -Force }
Copy-Item -LiteralPath $image -Destination (Join-Path $Workspace 'image.png') -Force
$config = @{
    title = '尖塔教练 / Spire AI Coach'
    description = [IO.File]::ReadAllText((Join-Path $root 'workshop/description.bbcode.txt'))
    visibility = 'public'
    changeNote = "v$($manifest.version) · AI / 本地整战规划；F8 面板、默认隐藏入口、整战探索 / 回合探索。AI advice and local battle planning; F8 panel, hidden by default, Battle Search / Turn Search."
    tags = @()
    dependencies = @()
    contentDescriptors = @()
}
[IO.File]::WriteAllText((Join-Path $Workspace 'workshop.json'), ($config | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$publishedId = Join-Path $root 'workshop/mod_id.txt'
if (Test-Path -LiteralPath $publishedId) {
    $id = [IO.File]::ReadAllText($publishedId).Trim()
    if ($id -notmatch '^\d+$') { throw 'Invalid published Workshop item ID.' }
    $destinationId = Join-Path $Workspace 'mod_id.txt'
    if ((Test-Path -LiteralPath $destinationId) -and [IO.File]::ReadAllText($destinationId).Trim() -ne $id) { throw 'Workshop workspace belongs to another item.' }
    [IO.File]::WriteAllText($destinationId, $id)
}
Write-Output "Prepared v$($manifest.version): $Workspace"
Write-Output 'Only the mod DLL and manifest are uploaded. Use the official sts2-mod-uploader to publish; reuse mod_id.txt for updates.'
