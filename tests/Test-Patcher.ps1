[CmdletBinding()]
param([Parameter(Mandatory)][string]$OriginalLauncher)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$originalHash = '6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386'
$patchedHash = 'f51c5c0211f3345c9d621c8bda946b3c5943c21290eddf4a70fe058e11892e27'
$backupSuffix = '.pcl-download.original.bak'
if ((Get-FileHash -LiteralPath $OriginalLauncher -Algorithm SHA256).Hash.ToLowerInvariant() -ne $originalHash) { throw 'Test fixture is not the verified official release.' }
$work = Join-Path $root ('artifacts\tests-' + [guid]::NewGuid().ToString('N'))
$standalone = Join-Path $work 'standalone'
New-Item -Path $standalone -ItemType Directory -Force | Out-Null
$tool = Join-Path $standalone 'PCL-Download-Patcher.exe'
Copy-Item -LiteralPath (Join-Path $root 'dist\PCL-Download-Patcher.exe') -Destination $tool
$target = Join-Path $standalone 'PCL Official 中文.exe'
Copy-Item -LiteralPath $OriginalLauncher -Destination $target
$script:taskTestCount = 0

function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash-Of([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Run-Tool([string]$Mode, [string]$Path, [bool]$Success = $true) {
    $script:taskTestCount++
    $report = Join-Path $work ("result-$script:taskTestCount.xml")
    $process = Start-Process -FilePath $tool -ArgumentList @($Mode, ('"' + $Path + '"'), '--report', ('"' + $report + '"')) -WorkingDirectory $standalone -PassThru -Wait -WindowStyle Hidden
    Assert-That (Test-Path -LiteralPath $report) "Missing report for $Mode (exit $($process.ExitCode))."
    [xml]$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8
    Assert-That (($process.ExitCode -eq 0) -eq $Success) "$Mode returned unexpected exit $($process.ExitCode): $($result.result.message)"
    Assert-That (($result.result.success -eq 'true') -eq $Success) "Unexpected report status for $Mode."
    return $result.result
}

$inspection = Run-Tool '--inspect' $target
Assert-That ($inspection.state -eq 'Original') 'Original launcher not recognized.'
$repaired = Run-Tool '--repair' $target
Assert-That ($repaired.state -eq 'Patched' -and $repaired.changed -eq 'true') 'Repair did not complete.'
Assert-That ((Hash-Of $target) -eq $patchedHash) 'Patched hash mismatch.'
$backup = $target + $backupSuffix
Assert-That ((Hash-Of $backup) -eq $originalHash) 'Automatic backup is not byte-identical.'
$backupTime = (Get-Item -LiteralPath $backup).LastWriteTimeUtc
$again = Run-Tool '--repair' $target
Assert-That ($again.changed -eq 'false' -and (Get-Item -LiteralPath $backup).LastWriteTimeUtc -eq $backupTime) 'Repeated repair changed the backup.'
$restored = Run-Tool '--restore' $target
Assert-That ($restored.state -eq 'Original' -and (Hash-Of $target) -eq $originalHash -and (Hash-Of $backup) -eq $originalHash) 'Restore did not recover exact original bytes.'
$again = Run-Tool '--restore' $target
Assert-That ($again.changed -eq 'false') 'Repeated restore was not a no-op.'

$unknown = Join-Path $standalone 'Unknown.exe'
$bytes = [IO.File]::ReadAllBytes($OriginalLauncher)
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($unknown, $bytes)
$unknownHash = Hash-Of $unknown
$null = Run-Tool '--repair' $unknown $false
Assert-That ((Hash-Of $unknown) -eq $unknownHash -and -not (Test-Path -LiteralPath ($unknown + $backupSuffix))) 'Unknown file was modified.'

$null = Run-Tool '--repair' $target
$badBackup = [IO.File]::ReadAllBytes($backup)
$badBackup[100] = $badBackup[100] -bxor 1
[IO.File]::WriteAllBytes($backup, $badBackup)
$null = Run-Tool '--restore' $target $false
Assert-That ((Hash-Of $target) -eq $patchedHash) 'Corrupt backup overwrote the patched file.'
Copy-Item -LiteralPath $OriginalLauncher -Destination $backup -Force
$null = Run-Tool '--restore' $target
# A corrupt existing backup also prevents a new repair from overwriting it.
[IO.File]::WriteAllBytes($backup, $badBackup)
$badBackupHash = Hash-Of $backup
$null = Run-Tool '--repair' $target $false
Assert-That ((Hash-Of $target) -eq $originalHash -and (Hash-Of $backup) -eq $badBackupHash) 'Existing corrupt backup was overwritten.'
Copy-Item -LiteralPath $OriginalLauncher -Destination $backup -Force

$null = Run-Tool '--repair' $target
$changed = [IO.File]::ReadAllBytes($target)
$changed[$changed.Length - 1] = $changed[$changed.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($target, $changed)
$changedHash = Hash-Of $target
$null = Run-Tool '--restore' $target $false
Assert-That ((Hash-Of $target) -eq $changedHash) 'An externally changed launcher was overwritten during restore.'
Copy-Item -LiteralPath $OriginalLauncher -Destination $target -Force

$locked = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try { $null = Run-Tool '--repair' $target $false } finally { $locked.Dispose() }
Assert-That ((Hash-Of $target) -eq $originalHash) 'Locked original changed.'
(Get-Item -LiteralPath $target).IsReadOnly = $true
try { $null = Run-Tool '--repair' $target $false } finally { (Get-Item -LiteralPath $target).IsReadOnly = $false }
Assert-That ((Hash-Of $target) -eq $originalHash) 'Read-only original changed.'

$null = Run-Tool '--repair' $target
Remove-Item -LiteralPath $target -Force
$null = Run-Tool '--restore' $target
Assert-That ((Hash-Of $target) -eq $originalHash) 'Missing launcher could not be recovered from backup.'
# Keep a patched copy for separate JIT and download verification.
$null = Run-Tool '--repair' $target
Assert-That ((Hash-Of $OriginalLauncher) -eq $originalHash) 'Source fixture was modified.'
Assert-That (@(Get-ChildItem -LiteralPath $standalone -Filter '*.tmp').Count -eq 0) 'Temporary files were left behind.'
@{ passed = $true; scenarios = $script:taskTestCount; standalone_exe = $tool; patched_launcher = $target; backup = $backup; original_sha256 = $originalHash; patched_sha256 = $patchedHash; tested_at_utc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\test-result.json') -Encoding UTF8
Write-Host "PASS: $script:taskTestCount standalone CLI checks, exact backup/restore, repeated operations, unsupported files, backup corruption, external changes, locks, read-only files and missing-file recovery."
