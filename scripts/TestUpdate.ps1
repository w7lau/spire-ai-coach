# Tests the delivery workflow in a disposable directory, with no game or SDK execution.
$ErrorActionPreference = 'Stop'
$fixtureParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$fixtureRoot = Join-Path $fixtureParent ('SpireAiCoach-UpdateTest-' + [Guid]::NewGuid().ToString('N'))
$marker = [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'scripts') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $fixtureRoot '.test-owner'), $marker)
foreach ($name in @('Update.ps1', 'Install.ps1', 'Build.ps1', 'ReleaseTools.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixtureRoot "scripts/$name")
}
Import-Module (Join-Path $fixtureRoot 'scripts/ReleaseTools.psm1') -Force -DisableNameChecking
$global:CoachUpdateFixture = @{
    root = $fixtureRoot; game = (Join-Path $fixtureRoot 'game'); tests = 0; builds = 0
    running = @(); failTests = $false; changeDuringTests = $false; changeDuringBuild = $false; replaceInstalledDuringBuild = $false
}
$savedGameDir = $env:STS2_GAME_DIR
$env:STS2_GAME_DIR = $null
$checks = 0

function Write-FixtureText([string]$RelativePath, [string]$Value) {
    $path = Join-Path $fixtureRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Value)
}
function Set-FixtureVersion([string]$Version) {
    Write-CoachJson (Join-Path $fixtureRoot 'SpireAiCoach.json') ([ordered]@{ id = 'SpireAiCoach'; version = $Version })
    Write-FixtureText 'src/SpireAiCoach.Mod/SpireAiCoach.Mod.csproj' "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>"
}
function Assert-Fixture([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
}
function Confirm-Fixture([string]$Message) { $script:checks++; Write-Output "PASS $checks - $Message" }
function Invoke-FixtureUpdate {
    param([switch]$InstallOnly, [switch]$BuildOnly, [switch]$ForceTests, [switch]$SkipTests, [switch]$Remembered, [switch]$WhatIf)
    $parameters = @{ InstallOnly = $InstallOnly; BuildOnly = $BuildOnly; ForceTests = $ForceTests; SkipTests = $SkipTests; WhatIf = $WhatIf }
    if (-not $Remembered) { $parameters.GameDir = $global:CoachUpdateFixture.game }
    & (Join-Path $fixtureRoot 'scripts/Update.ps1') @parameters *> (Join-Path $fixtureRoot 'last-run.log')
}
function Expect-FixtureFailure([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert-Fixture ($null -ne $failure -and $failure -like $Pattern) "Expected '$Pattern'; received '$failure'"
}

# These functions replace external commands only in this test process.
function global:dotnet {
    $arguments = @($args)
    $state = $global:CoachUpdateFixture
    $global:LASTEXITCODE = 0
    switch ($arguments[0]) {
        '--version' { Write-Output '9.0.fixture'; return }
        'run' {
            $state.tests++
            if ($state.changeDuringTests) {
                [IO.File]::AppendAllText((Join-Path $state.root 'src/SpireAiCoach.Core/Fixture.cs'), '// concurrent validation edit')
            }
            if ($state.failTests) { $global:LASTEXITCODE = 1; Write-Output 'Synthetic check failure'; return }
            Write-Output 'Synthetic core checks passed'; return
        }
        'build' {
            $state.builds++
            if ($state.changeDuringBuild) {
                [IO.File]::AppendAllText((Join-Path $state.root 'src/SpireAiCoach.Core/Fixture.cs'), '// concurrent build edit')
            }
            if ($state.replaceInstalledDuringBuild) {
                [IO.File]::WriteAllText((Join-Path $state.game 'mods/SpireAiCoach/SpireAiCoach.dll'), 'foreign update')
            }
            $output = Join-Path $state.root 'src/SpireAiCoach.Mod/bin/Release/net9.0/SpireAiCoach.dll'
            New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
            $inputs = Get-CoachFingerprint $state.root @('src', 'SpireAiCoach.json')
            [IO.File]::WriteAllText($output, "Synthetic DLL $inputs")
            Write-Output 'Synthetic build passed'; return
        }
        default { throw "Unexpected SDK invocation: $arguments" }
    }
}
function global:Get-CimInstance { return $global:CoachUpdateFixture.running }

try {
    Set-FixtureVersion '1.0.1'
    Write-FixtureText 'src/SpireAiCoach.Core/Fixture.cs' '// initial source'
    Write-FixtureText 'tests/SpireAiCoach.Tests/Fixture.cs' '// initial check'
    Write-FixtureText 'Directory.Build.props' '<Project />'
    foreach ($name in @('README.md', 'README.en.md', 'LICENSE', 'ACKNOWLEDGEMENTS.md', 'CONTRIBUTING.md')) { Write-FixtureText $name 'Fixture documentation' }
    Write-FixtureText 'game/SlayTheSpire2.exe' 'Not an executable'
    foreach ($name in @('sts2.dll', 'GodotSharp.dll', '0Harmony.dll')) { Write-FixtureText "game/data_sts2_windows_x86_64/$name" 'Not a game assembly' }
    Write-FixtureText 'scripts/RemoveLegacyWorkerCopies.ps1' "throw 'Cleanup must never run during installation.'"
    Write-FixtureText 'game/mods/SpireAiCoach/config.json' 'Synthetic private settings'
    Write-FixtureText 'game/profile/save.json' 'Synthetic save'
    $protected = @{}
    foreach ($name in @('game/mods/SpireAiCoach/config.json', 'game/profile/save.json')) { $protected[$name] = Get-CoachHash (Join-Path $fixtureRoot $name) }
    $destination = Join-Path $global:CoachUpdateFixture.game 'mods/SpireAiCoach'
    $dll = Join-Path $destination 'SpireAiCoach.dll'

    Invoke-FixtureUpdate
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq 1 -and $global:CoachUpdateFixture.builds -eq 1) 'Initial update must check and build once.'
    Assert-Fixture ((Get-CoachHash $dll) -eq (Get-CoachPackageState $fixtureRoot).dll_sha256) 'Installed DLL must match the package.'
    Confirm-Fixture 'Initial validation, build, packaging and installation'

    $global:CoachUpdateFixture.running = @([pscustomobject]@{ ExecutablePath = (Join-Path $global:CoachUpdateFixture.game 'SlayTheSpire2.exe') })
    Invoke-FixtureUpdate -Remembered
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq 1 -and $global:CoachUpdateFixture.builds -eq 1) 'Unchanged inputs must reuse both successful stages.'
    Assert-Fixture (@(Get-ChildItem -LiteralPath $destination -Filter '*.bak').Count -eq 0) 'Identical files need no new backup or replacement, even while running.'
    Confirm-Fixture 'Remembered game path, exact cache reuse and installation no-op'

    Set-FixtureVersion '1.0.2'
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Exit the game*'
    Assert-Fixture ((Read-CoachJson (Join-Path $destination 'SpireAiCoach.json')).version -eq '1.0.1') 'Running game must not be updated.'
    $counts = @($global:CoachUpdateFixture.tests, $global:CoachUpdateFixture.builds)
    $global:CoachUpdateFixture.running = @()
    Invoke-FixtureUpdate
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq $counts[0] -and $global:CoachUpdateFixture.builds -eq $counts[1]) 'Installation retry must not repeat checks/build.'
    Assert-Fixture (@(Get-ChildItem -LiteralPath $destination -Filter '*.bak').Count -eq 2) 'Both old files must be backed up.'
    Confirm-Fixture 'Running game refusal, retained evidence and fast retry'

    $global:CoachUpdateFixture.running = @([pscustomobject]@{ ExecutablePath = (Join-Path $fixtureRoot 'owned-worker/SlayTheSpire2.exe') })
    $linkedDll = Join-Path $fixtureRoot 'worker-linked.dll'
    New-Item -ItemType HardLink -Path $linkedDll -Target $dll | Out-Null
    $oldLinkedHash = Get-CoachHash $linkedDll
    Set-FixtureVersion '1.0.3'
    Invoke-FixtureUpdate
    Assert-Fixture ((Get-CoachHash $linkedDll) -eq $oldLinkedHash -and (Get-CoachHash $dll) -ne $oldLinkedHash) 'Atomic replacement must preserve linked worker contents.'
    $global:CoachUpdateFixture.running = @()
    Confirm-Fixture 'Other isolated workers do not block; linked DLL bytes remain unchanged'

    $installed = Get-CoachInstalledState $destination
    Set-FixtureVersion '1.0.4'
    $oldValidation = Read-CoachJson (Join-Path $fixtureRoot 'work/update-local/core-validation.json')
    $global:CoachUpdateFixture.failTests = $true
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Core checks failed*'
    Assert-CoachInstalledState $destination $installed
    Assert-Fixture ((Read-CoachJson (Join-Path $fixtureRoot 'work/update-local/core-validation.json')).input_sha256 -eq $oldValidation.input_sha256) 'Failed validation must not create a success stamp.'
    $global:CoachUpdateFixture.failTests = $false
    Confirm-Fixture 'Failed checks preserve the installation and do not publish validation evidence'

    $global:CoachUpdateFixture.changeDuringTests = $true
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Sources changed during validation*'
    $global:CoachUpdateFixture.changeDuringTests = $false
    Assert-CoachInstalledState $destination $installed
    Confirm-Fixture 'Concurrent source changes during validation refuse installation'

    $global:CoachUpdateFixture.changeDuringBuild = $true
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Inputs changed during build*'
    $global:CoachUpdateFixture.changeDuringBuild = $false
    Assert-CoachInstalledState $destination $installed
    Confirm-Fixture 'Concurrent source changes during build refuse installation'

    $originalDll = [IO.File]::ReadAllBytes($dll)
    $global:CoachUpdateFixture.replaceInstalledDuringBuild = $true
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Installed files changed*'
    Assert-Fixture ([IO.File]::ReadAllText($dll) -eq 'foreign update') 'An intervening installation must not be overwritten.'
    $global:CoachUpdateFixture.replaceInstalledDuringBuild = $false
    [IO.File]::WriteAllBytes($dll, $originalDll)
    Invoke-FixtureUpdate -InstallOnly
    Confirm-Fixture 'Intervening installation is preserved; prepared package supports InstallOnly'

    [IO.File]::AppendAllText((Join-Path $fixtureRoot 'dist/SpireAiCoach/SpireAiCoach.dll'), 'corruption')
    $installed = Get-CoachInstalledState $destination
    Expect-FixtureFailure { Invoke-FixtureUpdate -InstallOnly } '*Package/archive mismatch*'
    Assert-CoachInstalledState $destination $installed
    $beforeBuilds = $global:CoachUpdateFixture.builds
    Invoke-FixtureUpdate
    Assert-Fixture ($global:CoachUpdateFixture.builds -eq $beforeBuilds + 1) 'Invalid package cache must rebuild.'
    Confirm-Fixture 'Corrupt package is refused, then rebuilt by the normal entry point'

    $beforeTests = $global:CoachUpdateFixture.tests
    Invoke-FixtureUpdate -ForceTests -BuildOnly
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq $beforeTests + 1) 'ForceTests must execute the checks again.'
    Confirm-Fixture 'Explicit test rerun and package-only preparation'

    Set-FixtureVersion '1.0.5'
    $oldValidation = Read-CoachJson (Join-Path $fixtureRoot 'work/update-local/core-validation.json')
    $beforeTests = $global:CoachUpdateFixture.tests
    Invoke-FixtureUpdate -SkipTests -BuildOnly
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq $beforeTests) 'Explicit skip must not run checks.'
    Assert-Fixture ((Read-CoachJson (Join-Path $fixtureRoot 'work/update-local/core-validation.json')).input_sha256 -eq $oldValidation.input_sha256) 'Skipped checks must not publish success.'
    Confirm-Fixture 'Explicit skip is recorded without fabricating a passed check'

    $installed = Get-CoachInstalledState $destination
    $heldManifest = [IO.File]::Open((Join-Path $destination 'SpireAiCoach.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Expect-FixtureFailure { Invoke-FixtureUpdate -InstallOnly } '*Exception calling "Replace"*' }
    finally { $heldManifest.Dispose() }
    Assert-CoachInstalledState $destination $installed
    Assert-Fixture (@(Get-ChildItem -LiteralPath $destination -Filter '*.tmp').Count -eq 0) 'Failed partial replacement must remove owned staged files.'
    Confirm-Fixture 'Second-file replacement failure rolls back the DLL'

    Set-FixtureVersion '1.0.0'
    $counts = @($global:CoachUpdateFixture.tests, $global:CoachUpdateFixture.builds)
    Expect-FixtureFailure { Invoke-FixtureUpdate } '*Installed version is newer*'
    Assert-Fixture ($global:CoachUpdateFixture.tests -eq $counts[0] -and $global:CoachUpdateFixture.builds -eq $counts[1]) 'Downgrade must be refused before expensive work.'
    Assert-CoachInstalledState $destination $installed
    Confirm-Fixture 'Downgrade is refused before validation/build'

    $runCount = @(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'work/update-local/runs') -Directory).Count
    Invoke-FixtureUpdate -WhatIf
    Assert-Fixture (@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'work/update-local/runs') -Directory).Count -eq $runCount) 'WhatIf must not create update evidence or build/install.'
    foreach ($name in $protected.Keys) { Assert-Fixture ((Get-CoachHash (Join-Path $fixtureRoot $name)) -eq $protected[$name]) "Protected fixture changed: $name" }
    Confirm-Fixture 'WhatIf is read-only; saves and private settings remain unchanged'
    Write-Output "$checks workflow checks passed. SDK commands and game processes were simulated."
} finally {
    $env:STS2_GAME_DIR = $savedGameDir
    Remove-Item -LiteralPath 'Function:/dotnet', 'Function:/Get-CimInstance' -ErrorAction SilentlyContinue
    Remove-Variable -Name CoachUpdateFixture -Scope Global -ErrorAction SilentlyContinue
    Remove-Module ReleaseTools -ErrorAction SilentlyContinue
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($fixtureParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.File]::ReadAllText((Join-Path $resolvedFixture '.test-owner')) -eq $marker) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    } else { throw 'Fixture ownership check failed; cleanup was refused.' }
}
