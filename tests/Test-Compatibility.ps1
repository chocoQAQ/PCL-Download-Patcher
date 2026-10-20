[CmdletBinding()]
param([Parameter(Mandatory)][string]$OriginalLauncher, [Parameter(Mandatory)][string]$PatchedLauncher)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root ('artifacts\compatibility-' + [guid]::NewGuid().ToString('N'))
New-Item -Path $work -ItemType Directory -Force | Out-Null
$tool = Join-Path $work 'PCL-Download-Patcher.exe'
Copy-Item -LiteralPath "$root\dist\PCL-Download-Patcher.exe" -Destination $tool
Copy-Item -LiteralPath "$root\vendor\Mono.Cecil.dll" -Destination $work
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fixtureMaker = Join-Path $work 'CompatibilityFixtures.exe'
& $compiler /nologo /target:exe /codepage:65001 "/out:$fixtureMaker" "/reference:$root\vendor\Mono.Cecil.dll" "$root\src\EmbeddedDependencyResolver.cs" "$PSScriptRoot\CompatibilityFixtures.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compatibility fixture compilation failed.' }
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash-Of([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$script:taskCompatibilityCount = 0
function Run-Tool([string]$Mode, [string]$Path, [bool]$Success = $true) {
    $script:taskCompatibilityCount++
    $report = Join-Path $work ("result-$script:taskCompatibilityCount.xml")
    $process = Start-Process -FilePath $tool -ArgumentList @($Mode, ('"' + $Path + '"'), '--report', ('"' + $report + '"')) -PassThru -Wait -WindowStyle Hidden
    [xml]$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8
    Assert-That (($process.ExitCode -eq 0) -eq $Success) "$Mode unexpected exit $($process.ExitCode): $($result.result.message)"
    return $result.result
}
$variant = Join-Path $work 'Custom 中文 PCL.exe'
& $fixtureMaker $OriginalLauncher $variant variant
if ($LASTEXITCODE -ne 0) { throw 'Variant fixture creation failed.' }
$variantHash = Hash-Of $variant
$backup = $variant + '.pcl-download.original.bak'
$record = $variant + '.pcl-download.record.xml'
$result = Run-Tool '--inspect' $variant
Assert-That ($result.state -eq 'Original') 'Compatible customization was rejected.'
$result = Run-Tool '--repair' $variant
Assert-That ($result.state -eq 'Patched' -and $result.changed -eq 'true' -and $result.backupValid -eq 'true') 'Customized launcher repair failed.'
Assert-That ((Hash-Of $backup) -eq $variantHash) 'Customized original not backed up exactly.'
$variantPatchedHash = Hash-Of $variant
& "$root\artifacts\VerifyPreservation.exe" $backup $variant "$root\payload\DownloadMethods.dll"
if ($LASTEXITCODE -ne 0) { throw 'Customization was not preserved.' }
$result = Run-Tool '--repair' $variant
Assert-That ($result.changed -eq 'false') 'Repeated variant repair changed it.'
$result = Run-Tool '--restore' $variant
Assert-That ((Hash-Of $variant) -eq $variantHash) 'Variant restore did not preserve exact custom original.'
$null = Run-Tool '--repair' $variant
$backupBytes = [IO.File]::ReadAllBytes($backup)
$backupBytes[100] = $backupBytes[100] -bxor 1
[IO.File]::WriteAllBytes($backup, $backupBytes)
$null = Run-Tool '--restore' $variant $false
Assert-That ((Hash-Of $variant) -eq $variantPatchedHash) 'Corrupted variant backup was restored.'
$backupBytes[100] = $backupBytes[100] -bxor 1
[IO.File]::WriteAllBytes($backup, $backupBytes)
$recordBytes = [IO.File]::ReadAllBytes($record)
[IO.File]::WriteAllText($record, '<bad-record />')
$null = Run-Tool '--restore' $variant $false
Assert-That ((Hash-Of $variant) -eq $variantPatchedHash) 'Corrupted record allowed restore.'
[IO.File]::WriteAllBytes($record, $recordBytes)
$bytes = [IO.File]::ReadAllBytes($variant)
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($variant, $bytes)
$externallyChangedHash = Hash-Of $variant
$null = Run-Tool '--restore' $variant $false
Assert-That ((Hash-Of $variant) -eq $externallyChangedHash) 'Externally modified variant overwritten.'
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($variant, $bytes)
$null = Run-Tool '--restore' $variant
Copy-Item -LiteralPath $OriginalLauncher -Destination $backup -Force
$null = Run-Tool '--repair' $variant $false
Assert-That ((Hash-Of $variant) -eq $variantHash) 'Different build backup was overwritten.'
[IO.File]::WriteAllBytes($backup, $backupBytes)
$null = Run-Tool '--repair' $variant
Remove-Item -LiteralPath $variant
$null = Run-Tool '--restore' $variant
Assert-That ((Hash-Of $variant) -eq $variantHash) 'Variant missing-file recovery failed.'
# A changed download body is not a compatible unrelated customization.
$incompatible = Join-Path $work 'ChangedDownload.exe'
& $fixtureMaker $OriginalLauncher $incompatible incompatible
if ($LASTEXITCODE -ne 0) { throw 'Incompatible fixture creation failed.' }
$incompatibleHash = Hash-Of $incompatible
$result = Run-Tool '--inspect' $incompatible
Assert-That ($result.state -eq 'Unsupported') 'Altered download code accepted.'
$null = Run-Tool '--repair' $incompatible $false
Assert-That ((Hash-Of $incompatible) -eq $incompatibleHash -and -not (Test-Path -LiteralPath ($incompatible + '.pcl-download.original.bak'))) 'Incompatible download code changed.'
# Metadata-only old-version diagnosis must be a no-op, without running PCL.
$enabled = Join-Path $work 'LegacyEnabled.exe'
& $fixtureMaker $PatchedLauncher $enabled legacy-enabled
if ($LASTEXITCODE -ne 0) { throw 'Legacy enabled fixture creation failed.' }
$enabledHash = Hash-Of $enabled
$result = Run-Tool '--inspect' $enabled
Assert-That ($result.state -eq 'DownloadsEnabled' -and $result.version -eq '2.12.8.2') 'Enabled old version was misdiagnosed.'
$result = Run-Tool '--repair' $enabled
Assert-That ($result.changed -eq 'false' -and (Hash-Of $enabled) -eq $enabledHash) 'Enabled launcher was modified.'
Assert-That (-not (Test-Path -LiteralPath ($enabled + '.pcl-download.original.bak'))) 'No-op created an unnecessary backup.'
# Existing 1.0 backups did not have a receipt; retain their golden-hash restore path.
$legacy = Join-Path $work 'LegacyPatched.exe'
Copy-Item -LiteralPath $PatchedLauncher -Destination $legacy
Copy-Item -LiteralPath $OriginalLauncher -Destination ($legacy + '.pcl-download.original.bak')
$result = Run-Tool '--restore' $legacy
Assert-That ((Hash-Of $legacy) -eq (Hash-Of $OriginalLauncher)) 'Old 1.0 backup migration failed.'
@{ passed = $true; scenarios = $script:taskCompatibilityCount; tested_at_utc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath "$root\artifacts\compatibility-result.json" -Encoding UTF8
Write-Host "PASS: $script:taskCompatibilityCount compatibility checks; custom methods/resources preserved, exact restore, enabled old version no-op and incompatible download code rejected."
