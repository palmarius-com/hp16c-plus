# Reads the flash of a HP-15C Collector's Edition in programming mode (0x00000-0x1FFFF, 128 KB) TWICE with read
# commands only (sam_ba_read.ps1) and keeps the dump only if both reads are identical. Nothing is written to the
# calculator. About 2 minutes.
#
#   powershell -ExecutionPolicy Bypass -File dump_flash.ps1 [-Port COM6] [-Out my-15c-ce-dump.bin]
#
# Keep the dump: it is your backup, and the upload tools need it to build the image and to undo it.
param(
    [string]$Port = 'COM6',
    [string]$Out = 'my-15c-ce-dump.bin'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$reader = Join-Path $here 'sam_ba_read.ps1'
$tmpA = [System.IO.Path]::GetTempFileName()
$tmpB = [System.IO.Path]::GetTempFileName()
try {
    foreach ($t in @($tmpA, $tmpB)) {
        Write-Host "reading flash 0x00000-0x1FFFF ($t) ..."
        & powershell -NoProfile -ExecutionPolicy Bypass -File $reader -Port $Port -Start 0x0 -Words 32768 -Quiet -Ascii -BinOut $t
        if ((Get-Item $t).Length -ne 131072) { throw "the read stopped early ($((Get-Item $t).Length) bytes). Put the calculator into programming mode again and start over." }
    }
    $a = [System.IO.File]::ReadAllBytes($tmpA)
    $b = [System.IO.File]::ReadAllBytes($tmpB)
    if (-not [System.Linq.Enumerable]::SequenceEqual($a, $b)) { throw 'the two reads differ: nothing saved. Check the cable and start over.' }
    [System.IO.File]::WriteAllBytes($Out, $a)
    $h = ([BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($a))).Replace('-', '')
    Write-Host "saved $Out (131072 bytes, two identical reads, SHA-256 $h)"
    Write-Host 'Keep this file: it is your backup.'
}
finally {
    Remove-Item -ErrorAction SilentlyContinue $tmpA, $tmpB
}
