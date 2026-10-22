param([string]$PackageDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
# Read-only file verification. No serial port, programmer, configuration, or device access.
# PackageDirectory contains SHA256SUMS.txt; every listed path must remain below it.
$root = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$manifest = Join-Path $root 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'SHA256SUMS.txt is missing.' }
$count = 0
foreach ($line in [IO.File]::ReadAllLines($manifest, [Text.Encoding]::UTF8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Malformed SHA-256 manifest line.' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    if ([IO.Path]::IsPathRooted($relative)) { throw 'Absolute paths are not allowed in the manifest.' }
    $file = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $file.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes the package directory.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file: $relative" }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($file)
    try { $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
    if ($actual -ne $expected) { throw "SHA-256 mismatch: $relative" }
    $count++
}
if ($count -eq 0) { throw 'The manifest contains no files.' }
Write-Output "Verified $count files. This confirms file integrity, not hardware acceptance."
