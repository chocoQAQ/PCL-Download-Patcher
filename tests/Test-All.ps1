[CmdletBinding()]
param([string]$OriginalLauncher = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $OriginalLauncher) { $OriginalLauncher = & "$PSScriptRoot/Get-OriginalLauncher.ps1" }
& "$root/Build.ps1"
& "$PSScriptRoot/Test-Patcher.ps1" -OriginalLauncher $OriginalLauncher
$result = Get-Content -LiteralPath "$root/artifacts/test-result.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $result.passed) { throw 'Patcher checks did not pass.' }
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$verifier = "$root\artifacts\VerifyPreservation.exe"
$arguments = @('/nologo', '/target:exe', '/codepage:65001', "/out:$verifier", "/reference:$root\vendor\Mono.Cecil.dll")
$arguments += @("$root\src\PatchProfile.cs", "$root\src\MethodTransplant.cs", "$PSScriptRoot\VerifyPreservation.cs")
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Preservation verifier compilation failed.' }
Copy-Item -LiteralPath "$root/vendor/Mono.Cecil.dll" -Destination "$root/artifacts/Mono.Cecil.dll" -Force
& $verifier $OriginalLauncher $result.patched_launcher "$root/payload/DownloadMethods.dll" 2>&1 | Tee-Object -FilePath "$root/artifacts/preservation.log"
if ($LASTEXITCODE -ne 0) { throw 'Unrelated launcher methods or resources changed.' }
& "$PSScriptRoot/Test-Compatibility.ps1" -OriginalLauncher $OriginalLauncher -PatchedLauncher $result.patched_launcher
$compatibility = Get-Content -LiteralPath "$root/artifacts/compatibility-result.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $compatibility.passed) { throw 'Compatibility checks did not pass.' }
& "$PSScriptRoot/Test-Launcher.ps1" -PatchedLauncher $result.patched_launcher
@{
    passed = $true
    patcher_checks = $result.scenarios + $compatibility.scenarios
    compatibility_checks = $compatibility.scenarios
    unrelated_methods_and_resources_preserved = $true
    launcher_jit_and_download_verified = $true
    tested_at_utc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath "$root/artifacts/verification.json" -Encoding UTF8
Write-Host 'PASS: build, backup/restore, code preservation and actual Minecraft downloads.'
