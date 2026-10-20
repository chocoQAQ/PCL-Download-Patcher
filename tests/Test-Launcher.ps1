[CmdletBinding()]
param([Parameter(Mandatory)][string]$PatchedLauncher)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'artifacts\launcher-test'
$dependencies = Join-Path $work 'compile-dependencies'
New-Item -Path $work, $dependencies -ItemType Directory -Force | Out-Null
$framework = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$compiler = Join-Path $framework 'csc.exe'
$extractor = Join-Path $root 'artifacts\ExtractTestDependencies.exe'
& $compiler /nologo /target:exe "/out:$extractor" "/reference:$root\vendor\Mono.Cecil.dll" "$root\tools\ExtractTestDependencies.cs"
if ($LASTEXITCODE -ne 0) { throw 'Dependency extractor compilation failed.' }
Copy-Item -LiteralPath "$root\vendor\Mono.Cecil.dll" -Destination "$root\artifacts\Mono.Cecil.dll" -Force
& $extractor $PatchedLauncher $dependencies
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect launcher dependencies.' }
Copy-Item -LiteralPath $PatchedLauncher -Destination "$work\Plain Craft Launcher 2.exe" -Force
$references = @(
    "$work\Plain Craft Launcher 2.exe", "$dependencies\PCLCS.dll", "$dependencies\MeloongCore.dll", "$dependencies\MeloongCore.Wpf.dll", "$dependencies\Newtonsoft.Json.dll",
    "$framework\WPF\WindowsBase.dll", "$framework\WPF\PresentationCore.dll", "$framework\WPF\PresentationFramework.dll", "$framework\System.Xaml.dll",
    "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\netstandard.dll"
)
$arguments = @('/nologo', '/target:exe', '/platform:anycpu', "/out:$work\LauncherSmoke.exe")
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += "$PSScriptRoot\LauncherSmoke.cs"
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Launcher smoke test compilation failed.' }
# The executable runs against its embedded libraries, with no config or DLLs beside it.
& "$work\LauncherSmoke.exe" $work '1.21.1' 'false' 2>&1 | Tee-Object -FilePath "$work\smoke.log"
if ($LASTEXITCODE -ne 0) { throw 'Patched launcher failed JIT or download verification.' }
