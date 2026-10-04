[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = (Resolve-Path -LiteralPath $GameDir).Path
$ownedBase = Join-Path (Split-Path -Parent $source) '.spire-ai-coach-ui-checks'
$root = Join-Path $ownedBase ('presentation-smoke-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$game = Join-Path $root 'game'
New-Item -ItemType Directory -Path $game | Out-Null
Set-Content -LiteralPath (Join-Path $root '.spire-native-probe-owner') -Value 'owned presentation and native death callback check'
function Share-Tree([string]$From, [string]$To) {
    $resolved = [IO.Path]::GetFullPath($To)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe private target' }
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    foreach ($f in Get-ChildItem -LiteralPath $From -File) { New-Item -ItemType HardLink -Path (Join-Path $To $f.Name) -Value $f.FullName | Out-Null }
    foreach ($d in Get-ChildItem -LiteralPath $From -Directory) {
        if (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked source directory' }
        Share-Tree $d.FullName (Join-Path $To $d.Name)
    }
}
foreach ($f in Get-ChildItem -LiteralPath $source -File) {
    if ($f.Extension -in @('.exe','.dll','.pck','.json')) { New-Item -ItemType HardLink -Path (Join-Path $game $f.Name) -Value $f.FullName | Out-Null }
}
Share-Tree (Join-Path $source 'data_sts2_windows_x86_64') (Join-Path $game 'data_sts2_windows_x86_64')
$coach = Join-Path $game 'mods\SpireAiCoach'; $integration = Join-Path $game 'mods\SpireLocalIntegration'
New-Item -ItemType Directory -Path $coach,$integration | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'src\SpireAiCoach.Mod\bin\Release\net9.0\SpireAiCoach.dll') -Destination $coach
Copy-Item -LiteralPath (Join-Path $repo 'SpireAiCoach.json') -Destination $coach
Copy-Item -LiteralPath (Join-Path $repo 'experiments\LocalIntegration\bin\Release\net9.0\SpireLocalIntegration.dll') -Destination $integration
@{ id='SpireLocalIntegration'; name='Private presentation check'; author='w7lau'; version='0.0.1'; has_dll=$true; has_pck=$false; affects_gameplay=$false;
    dependencies=@(@{id='SpireAiCoach'; min_version='0.7.22'}) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $integration 'SpireLocalIntegration.json')
$settings = Join-Path $root 'Roaming\SlayTheSpire2\default\1'; New-Item -ItemType Directory -Path $settings -Force | Out-Null
'{"volume_master":0,"volume_bgm":0,"volume_sfx":0,"volume_ambience":0,"skip_intro_logo":true,"mod_settings":{"mods_enabled":true,"mod_list":[]}}' | Set-Content -LiteralPath (Join-Path $settings 'settings.save')
$before = (Get-FileHash -LiteralPath (Join-Path $source 'mods\SpireAiCoach\SpireAiCoach.dll') -Algorithm SHA256).Hash
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $game 'SlayTheSpire2.exe'))
$start.WorkingDirectory = $game; $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach($arg in @('--rendering-method','gl_compatibility','--rendering-driver','opengl3','--resolution','1920x1080','--position','-32000,-32000','--audio-driver','Dummy','--force-steam=off','--max-fps','120','--log-file',(Join-Path $root 'game.log'))) { $start.ArgumentList.Add($arg) }
$start.Environment['APPDATA'] = Join-Path $root 'Roaming'; $start.Environment['LOCALAPPDATA'] = Join-Path $root 'Local'
$start.Environment['SPIRE_LOCAL_INTEGRATION'] = $root; $start.Environment['SPIRE_LOCAL_PRESENTATION_SMOKE'] = '1'
[void]$start.Environment.Remove('SPIRE_COACH_WORKER'); [void]$start.Environment.Remove('SPIRE_NATIVE_PROBE_ROOT')
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
Write-Output ('Private native presentation check: ' + $root)
try {
    if (-not $process.WaitForExit(90000)) { throw 'Private presentation check timed out' }
    [IO.File]::WriteAllText((Join-Path $root 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $root 'stderr.log'), $stderr.GetAwaiter().GetResult())
    if ((Get-FileHash -LiteralPath (Join-Path $source 'mods\SpireAiCoach\SpireAiCoach.dll') -Algorithm SHA256).Hash -ne $before) { throw 'Installed DLL changed' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'integration-success'))) {
        if (Test-Path -LiteralPath (Join-Path $root 'integration-error.txt')) { Get-Content -LiteralPath (Join-Path $root 'integration-error.txt') }
        throw 'Private presentation check failed'
    }
    Get-Content -LiteralPath (Join-Path $root 'integration-presentation-summary.json')
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) }
    $process.Dispose()
}
