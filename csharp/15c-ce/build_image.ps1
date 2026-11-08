# Builds the 16C+ flash image for a HP-15C Collector's Edition from THAT calculator's own flash dump and the
# 16cplus.patch of this kit. Nothing is sent to the calculator; no HP code is in the kit: the image is made here,
# on your PC, from your own dump, and stays here.
#
#   powershell -ExecutionPolicy Bypass -File build_image.ps1 -Dump my-dump.bin [-Patch 16cplus.patch] [-OutDir .]
#
# What it does
#   1. checks the dump (131072 bytes, flash 0x00000-0x1FFFF) against known-units.txt: the bootloader, the vector
#      table, the 16C ROM, the 15C ROM copy that will be replaced, the three ROM pointer literals and the firmware code
#      must have the SHA-256 of the unit the kit was tried on. A dump that differs is refused: the kit has been
#      tried on one unit only, and the addresses it writes are those of that unit's firmware;
#   2. takes the unit's own 16C ROM (0x12180, 6144 words of 16 bits), puts the words of the patch over it (31 words in
#      0x0000-0x17FF, the code for 0x2000-0x37FF) and sets the checksum word 0x13FF so that the ROM sums (mod 1023)
#      to the same value as the unit's original 16C ROM;
#   3. writes ce-16cplus.bin (28672 bytes: Nut words 0000-17FF, 1800-1FFF zero, 2000-37FF, as 16-bit LE halfwords) for
#      the slot 0xB180-0x1217F, and ptr16c-to-B180.bin (4 bytes) for the literal at 0x1CD10.
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [string]$Patch = '',
    [string]$OutDir = '.',
    [string]$Known = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Patch -eq '') { $Patch = Join-Path $here '16cplus.patch' }
if ($Known -eq '') { $Known = Join-Path $here 'known-units.txt' }

function Sha256Hex([byte[]]$bytes, [int]$offset, [int]$count) {
    $h = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes, $offset, $count)
    return ([BitConverter]::ToString($h)).Replace('-', '').ToLower()
}

$d = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Dump).Path)
if ($d.Length -ne 131072) { throw "the dump must be 131072 bytes (flash 0x00000-0x1FFFF), this one has $($d.Length)" }

# 1. known unit
$bad = New-Object System.Collections.Generic.List[string]
$checked = 0
foreach ($l in Get-Content -LiteralPath $Known) {
    if ($l -match '^([0-9A-Fa-f]{5}) ([0-9A-Fa-f]{5}) ([0-9a-f]{64})  (.*)$') {
        $s = [Convert]::ToInt32($matches[1], 16); $n = [Convert]::ToInt32($matches[2], 16)
        $checked++
        if ((Sha256Hex $d $s $n) -ne $matches[3]) { $bad.Add(('{0:X5}+{1:X5}  {2}' -f $s, $n, $matches[4])) }
    }
}
if ($checked -eq 0) { throw "no regions found in $Known" }
if ($bad.Count -gt 0) {
    Write-Host 'This dump is not a unit the kit knows. These regions differ from the unit it was tried on:'
    $bad | ForEach-Object { Write-Host "   $_" }
    Write-Host 'Nothing was built. Do not write anything to this calculator with this kit.'
    throw 'unknown unit'
}
Write-Host "dump checked: $checked regions equal to the known unit"

# 2. patch words
$p = @{}
foreach ($l in Get-Content -LiteralPath (Resolve-Path -LiteralPath $Patch).Path) {
    if ($l -match '^([0-9a-fA-F]{4}):([0-9a-fA-F]{3})\b') {
        $a = [Convert]::ToInt32($matches[1], 16); $w = [Convert]::ToInt32($matches[2], 16)
        if (($a -ge 0x1800 -and $a -lt 0x2000) -or $a -ge 0x3800) { throw ('patch word outside the two ROM ranges: {0:x4}' -f $a) }
        $p[$a] = $w
    }
}
$n0 = @($p.Keys | Where-Object { $_ -lt 0x1800 }).Count
$n1 = @($p.Keys | Where-Object { $_ -ge 0x2000 }).Count
if ($n0 -lt 20 -or $n1 -lt 5000) { throw "this does not look like the 16C+ patch ($n0 words in 0000-17FF, $n1 in 2000-37FF)" }
Write-Host "patch: $n0 words in the 16C ROM range, $n1 words in 2000-37FF"

# 3. image
$mod = 1023
$ce = New-Object 'int[]' 0x1800
$sumCe = 0
for ($a = 0; $a -lt 0x1800; $a++) { $ce[$a] = [BitConverter]::ToUInt16($d, 0x12180 + 2 * $a); $sumCe += $ce[$a] }
$sumCe = $sumCe % $mod
$new0 = $ce.Clone()
foreach ($a in $p.Keys) { if ($a -lt 0x1800) { $new0[$a] = $p[$a] } }
$sumNew = 0
for ($a = 0; $a -lt 0x1800; $a++) { $sumNew += $new0[$a] }
$adj = ((($sumCe - ($sumNew % $mod)) % $mod) + $mod) % $mod
$old = $new0[0x13FF]
$new0[0x13FF] = ($old + $adj) % $mod
$chk = 0
for ($a = 0; $a -lt 0x1800; $a++) { $chk += $new0[$a] }
if (($chk % $mod) -ne $sumCe) { throw 'checksum adjustment failed' }
Write-Host ("chip-0 sum mod 1023: unit's own 16C ROM {0}; with the patch words {1}; after the checksum word 0x13FF ({2:x3} -> {3:x3}) {4}" -f $sumCe, ($sumNew % $mod), $old, $new0[0x13FF], ($chk % $mod))

$img = New-Object byte[] 0x7000
for ($w = 0; $w -lt 0x1800; $w++) { $img[2 * $w] = [byte]($new0[$w] -band 0xFF); $img[2 * $w + 1] = [byte]($new0[$w] -shr 8) }
for ($w = 0x2000; $w -lt 0x3800; $w++) {
    $v = 0
    if ($p.ContainsKey($w)) { $v = $p[$w] }
    $img[2 * $w] = [byte]($v -band 0xFF); $img[2 * $w + 1] = [byte]($v -shr 8)
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$imgFile = Join-Path $OutDir 'ce-16cplus.bin'
$ptrFile = Join-Path $OutDir 'ptr16c-to-B180.bin'
[System.IO.File]::WriteAllBytes($imgFile, $img)
[System.IO.File]::WriteAllBytes($ptrFile, [byte[]](0x80, 0xB1, 0x00, 0x00))
Write-Host ("image   {0}  ({1} bytes, SHA-256 {2})" -f $imgFile, $img.Length, (Sha256Hex $img 0 $img.Length))
Write-Host ("pointer {0}  (4 bytes: 0xB180 for the literal at 0x1CD10, now 0x12180)" -f $ptrFile)
Write-Host 'Next: upload.ps1 (read-only without -Execute).'
