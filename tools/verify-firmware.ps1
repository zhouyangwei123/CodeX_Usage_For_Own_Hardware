param([string]$OriginalSource)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo 'firmware\STM32G474'
$manifest = Get-Content -LiteralPath (Join-Path $repo 'docs\firmware-baseline.sha256')
$count = 0
foreach ($line in $manifest) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid firmware manifest entry' }
    $expected = $Matches[1]
    $relative = $Matches[2]
    $path = Join-Path $root $relative
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Firmware changed: $relative" }
    if ($OriginalSource) {
        $original = Join-Path $OriginalSource $relative
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Original firmware changed: $relative" }
    }
    $count++
}
$actual = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
if ($actual.Count -ne $count) { throw 'Unexpected added or missing firmware file' }
Write-Output "PASS: $count firmware files match the imported SHA-256 baseline."
