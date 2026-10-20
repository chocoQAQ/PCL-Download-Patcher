[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$inputs = @(
    @{ Path = 'vendor/Mono.Cecil.dll'; Hash = 'c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b' },
    @{ Path = 'payload/DownloadMethods.dll'; Hash = '50af0fd2788a386c2f433ca20e7d305a211e83bb2a9a3b4a26387e8eed815e8e' }
)
foreach ($inputFile in $inputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $inputFile.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputFile.Hash) {
        throw "Unreviewed dependency or patch payload: $($inputFile.Path)"
    }
}
$output = Join-Path $root 'dist'
New-Item -Path $output -ItemType Directory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8.' }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$output\PCL-Download-Patcher.exe", "/win32manifest:$root\app.manifest")
$arguments += @('System.Windows.Forms', 'System.Drawing', 'System.Xml.Linq') | ForEach-Object { "/reference:$_.dll" }
$arguments += "/reference:$root\vendor\Mono.Cecil.dll"
$arguments += "/resource:$root\vendor\Mono.Cecil.dll,Mono.Cecil"
$arguments += "/resource:$root\payload\DownloadMethods.dll,DownloadMethods"
$arguments += "/resource:$root\vendor\Mono.Cecil.LICENSE.txt,CecilLicense"
$arguments += "/resource:$root\PCL-LICENCE.txt,PclLicense"
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Join-Path $output 'PCL-Download-Patcher.exe'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  PCL-Download-Patcher.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
$profile = Get-Content -LiteralPath "$root/src/PatchProfile.cs" -Raw -Encoding UTF8
function Get-ProfileString([string]$Name) {
    $match = [regex]::Match($profile, ('const string ' + [regex]::Escape($Name) + ' = "([^"]+)"'))
    if (-not $match.Success) { throw "Missing profile field: $Name" }
    return $match.Groups[1].Value
}
@{
    repository = 'https://github.com/chocoQAQ/PCL-Download-Patcher'
    commit = $env:GITHUB_SHA
    supported_version = Get-ProfileString 'Version'
    compatibility = 'Verified download-method signatures; unrelated customizations permitted; enabled download entries detected without modification'
    original_sha256 = Get-ProfileString 'OriginalSha256'
    patched_sha256 = Get-ProfileString 'PatchedSha256'
    tool_sha256 = $hash
    built_at_utc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding UTF8
Write-Host "Built $exe"
