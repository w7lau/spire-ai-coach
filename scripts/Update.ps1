[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$GameDir = $env:STS2_GAME_DIR,
    [switch]$InstallOnly,
    [switch]$BuildOnly,
    [switch]$ForceTests,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if ($InstallOnly -and ($BuildOnly -or $ForceTests -or $SkipTests)) { throw '-InstallOnly cannot be combined with build/test options.' }
if ($ForceTests -and $SkipTests) { throw 'Choose either -ForceTests or -SkipTests.' }
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force -DisableNameChecking
$game = Resolve-CoachGameDir $root $GameDir
if (-not $PSCmdlet.ShouldProcess($game, 'Validate, package and install Spire AI Coach')) { return }
$lock = Enter-CoachReleaseLock $root
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDir = Join-Path $root "work/update-local/runs/$runId"
$timeline = New-Object 'Collections.Generic.List[object]'
$wall = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{ status = 'running'; game_dir = $game; native_game_validated = $false; stages = $timeline }

function Invoke-UpdateStep([string]$Name, [scriptblock]$Action) {
    $start = $wall.Elapsed.TotalMilliseconds
    Write-Host ('[{0:N2}s] {1}' -f $wall.Elapsed.TotalSeconds, $Name)
    try { & $Action }
    finally { $timeline.Add([ordered]@{ name = $Name; start_ms = [Math]::Round($start); elapsed_ms = [Math]::Round($wall.Elapsed.TotalMilliseconds - $start) }) }
}

try {
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    $destination = Get-CoachInstallPath $game
    $baseline = Get-CoachInstalledState $destination
    $baselinePath = Join-Path $runDir 'installed-before.json'
    Write-CoachJson $baselinePath $baseline
    if (-not $InstallOnly) {
        $manifest = Read-CoachJson (Join-Path $root 'SpireAiCoach.json')
        [xml]$project = [IO.File]::ReadAllText((Join-Path $root 'src/SpireAiCoach.Mod/SpireAiCoach.Mod.csproj'))
        if ($manifest.version -ne [string]$project.Project.PropertyGroup.Version) { throw 'Source project and manifest versions differ.' }
        if (-not $BuildOnly -and $baseline.version -and ([Version]$manifest.version -lt [Version]$baseline.version)) {
            throw 'Installed version is newer than this checkout. Incorporate its changes before updating.'
        }
        $sdk = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'The .NET SDK is unavailable.' }
        $coreScopes = @('src', 'tests', 'Directory.Build.props', 'Directory.Build.targets', 'GamePath.props', 'global.json', 'NuGet.Config', 'SpireAiCoach.json')
        $coreValues = @('core-validation-v1', $sdk)
        $coreInput = Get-CoachFingerprint $root $coreScopes $coreValues
        $coreCachePath = Join-Path $root 'work/update-local/core-validation.json'
        $result.core_validation = Invoke-UpdateStep 'Core validation' {
            $cached = try { Read-CoachJson $coreCachePath } catch { $null }
            if ($SkipTests) { Write-Host 'Tests explicitly skipped; no validation stamp will be created.'; return [ordered]@{ mode = 'skipped' } }
            if (-not $ForceTests -and $cached -and $cached.passed -and $cached.input_sha256 -eq $coreInput) {
                Write-Host 'Reusing passed checks for these exact sources and SDK.'
                return [ordered]@{ mode = 'cached'; evidence = $cached.log }
            }
            $log = Join-Path $runDir 'core-tests.log'
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & dotnet run --project (Join-Path $root 'tests/SpireAiCoach.Tests') -c Release *> $log
                $testExit = $LASTEXITCODE
            } finally { $ErrorActionPreference = $previousPreference }
            if ($testExit -ne 0) { throw "Core checks failed; installation was not started. See $log" }
            if ((Get-CoachFingerprint $root $coreScopes $coreValues) -ne $coreInput) { throw 'Sources changed during validation. Re-run the update.' }
            Write-CoachJson $coreCachePath ([ordered]@{ input_sha256 = $coreInput; passed = $true; sdk = $sdk; utc = [DateTime]::UtcNow.ToString('o'); log = $log })
            return [ordered]@{ mode = 'fresh'; evidence = $log }
        }
        $buildScopes = @('src', 'Directory.Build.props', 'Directory.Build.targets', 'GamePath.props', 'global.json', 'NuGet.Config', 'SpireAiCoach.json', 'scripts/Build.ps1', 'README.md', 'README.en.md', 'LICENSE', 'ACKNOWLEDGEMENTS.md', 'CONTRIBUTING.md')
        $buildValues = @('build-package-v1', $sdk)
        foreach ($name in @('sts2.dll', 'GodotSharp.dll', '0Harmony.dll')) {
            $buildValues += $name + ':' + (Get-CoachHash (Join-Path $game "data_sts2_windows_x86_64/$name"))
        }
        $buildInput = Get-CoachFingerprint $root $buildScopes $buildValues
        $buildCachePath = Join-Path $root 'work/update-local/build.json'
        $package = Invoke-UpdateStep 'Build and package' {
            $cached = try { Read-CoachJson $buildCachePath } catch { $null }
            if ($cached -and $cached.input_sha256 -eq $buildInput) {
                $existing = try { Get-CoachPackageState $root } catch { $null }
                if ($existing -and (($existing | ConvertTo-Json -Compress) -eq ($cached.package | ConvertTo-Json -Compress))) {
                    Write-Host 'Reusing the matching package; archive and DLL hashes verified.'
                    return $existing
                }
            }
            $log = Join-Path $runDir 'build.log'
            & (Join-Path $PSScriptRoot 'Build.ps1') -GameDir $game -SkipTests *> $log
            if ((Get-CoachFingerprint $root $coreScopes $coreValues) -ne $coreInput -or
                (Get-CoachFingerprint $root $buildScopes $buildValues) -ne $buildInput) { throw 'Inputs changed during build. Re-run the update.' }
            $built = Get-CoachPackageState $root
            if ($built.dll_sha256 -ne (Get-CoachHash (Join-Path $root 'src/SpireAiCoach.Mod/bin/Release/net9.0/SpireAiCoach.dll'))) {
                throw 'Packaged DLL differs from the build output.'
            }
            Write-CoachJson $buildCachePath ([ordered]@{ input_sha256 = $buildInput; package = $built; utc = [DateTime]::UtcNow.ToString('o') })
            return $built
        }
    } else {
        $result.core_validation = [ordered]@{ mode = 'not-run-install-only' }
        $package = Invoke-UpdateStep 'Check existing package' { Get-CoachPackageState $root }
    }
    if (-not $InstallOnly) {
        $finalBuildValues = @('build-package-v1', $sdk)
        foreach ($name in @('sts2.dll', 'GodotSharp.dll', '0Harmony.dll')) {
            $finalBuildValues += $name + ':' + (Get-CoachHash (Join-Path $game "data_sts2_windows_x86_64/$name"))
        }
        if ((Get-CoachFingerprint $root $coreScopes $coreValues) -ne $coreInput -or
            (Get-CoachFingerprint $root $buildScopes $finalBuildValues) -ne $buildInput) {
            throw 'Inputs changed before delivery. Re-run the update.'
        }
    }
    $result.package = $package
    if ($BuildOnly) { $result.status = 'prepared' }
    else {
        Invoke-UpdateStep 'Install' {
            & (Join-Path $PSScriptRoot 'Install.ps1') -GameDir $game -ExpectedStatePath $baselinePath -ReceiptPath (Join-Path $runDir 'installation.json')
        }
        $result.status = 'done'
    }
    Write-CoachJson (Join-Path $root 'work/update-local/last-target.json') ([ordered]@{ game_dir = $game })
} catch {
    $result.status = 'failed'
    $result.error = $_.Exception.Message
    throw
} finally {
    try {
        $result.elapsed_ms = [Math]::Round($wall.Elapsed.TotalMilliseconds)
        Write-CoachJson (Join-Path $runDir 'timeline.json') $result
        Write-Host ('{0} in {1:N2}s. Timeline: {2}' -f $result.status, $wall.Elapsed.TotalSeconds, (Join-Path $runDir 'timeline.json'))
    } finally { $lock.ReleaseMutex(); $lock.Dispose() }
}
