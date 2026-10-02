[CmdletBinding(SupportsShouldProcess)]
param([string[]]$CacheRoots = @((Join-Path $env:APPDATA 'SlayTheSpire2/spire_ai_coach/local-workers')))
$ErrorActionPreference = 'Stop'
foreach ($cacheRoot in $CacheRoots) {
    if (-not (Test-Path -LiteralPath $cacheRoot)) { continue }
    $base = (Resolve-Path -LiteralPath $cacheRoot).Path.TrimEnd('\')
    foreach ($bucket in Get-ChildItem -LiteralPath $base -Directory) {
        if ($bucket.Name -notmatch '^[0-9A-F]{16}$' -or ($bucket.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        foreach ($worker in Get-ChildItem -LiteralPath $bucket.FullName -Directory) {
            if ($worker.Name -notmatch '^worker-[0-9]+$' -or ($worker.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
            $marker = Join-Path $worker.FullName '.coach-worker'
            if (-not (Test-Path -LiteralPath $marker)) { continue }
            if ([IO.File]::ReadAllText($marker) -ne 'SpireAiCoach local worker v1') { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $worker.FullName 'game'))
            if (-not $target.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup path escaped the named cache root.' }
            if (-not (Test-Path -LiteralPath $target)) { continue }
            # Never recurse through a junction/symlink, including an unexpected link in an old copy.
            $items = @((Get-Item -LiteralPath $target)) + @(Get-ChildItem -LiteralPath $target -Recurse -Force)
            if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Linked legacy tree refused: $target" }
            $lease = $null
            try {
                try { $lease = [IO.File]::Open((Join-Path $worker.FullName '.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
                catch [IO.IOException] { Write-Warning "Worker is in use; retained: $target"; continue }
                if ($PSCmdlet.ShouldProcess($target, 'Remove owned legacy game resource copy; keep diagnostics and private saves')) {
                    $bytes = ($items | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum
                    Remove-Item -LiteralPath $target -Recurse -Force
                    Write-Output "Removed legacy copy: $target ($bytes bytes)"
                }
            } finally { if ($null -ne $lease) { $lease.Dispose() } }
        }
    }
}
