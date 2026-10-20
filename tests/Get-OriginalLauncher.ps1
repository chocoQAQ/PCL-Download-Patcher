[CmdletBinding()]
param([string]$ArchivePath = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'artifacts\official-fixture'
New-Item -Path $work -ItemType Directory -Force | Out-Null
$expectedArchiveHash = '01877f06df84cbab376c286a633e76ef7fca0bb469e26df97f3b09b5abd503bc'
$expectedExeHash = '6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386'
$original = Join-Path $work 'OriginalPCL.exe'
if ((Test-Path -LiteralPath $original) -and (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedExeHash) {
    Write-Output $original
    exit 0
}
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $work 'official-2.99.0.0.zip'
    $uri = 'https://raw.githubusercontent.com/Meloong-Git/PCL/6b9a50df911c9d68b69b24c979efba477fb14b67/%E6%9C%80%E6%96%B0%E6%AD%A3%E5%BC%8F%E7%89%88.zip'
    Invoke-WebRequest -Uri $uri -OutFile $ArchivePath -MaximumRetryCount 2 -RetryIntervalSec 3
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedArchiveHash) {
    throw 'Official fixture archive SHA-256 does not match the pinned release.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    $entry = $archive.GetEntry('Plain Craft Launcher 2.exe')
    if (-not $entry -or $entry.Length -ne 5980160) { throw 'Unexpected official archive contents.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $original, $true)
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedExeHash) {
    throw 'Official launcher SHA-256 does not match the supported profile.'
}
Write-Host 'Pinned official PCL 2.99.0.0 fixture verified.'
Write-Output $original
