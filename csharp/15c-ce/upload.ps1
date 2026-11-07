# Writes the 16C+ image into a HP-15C Collector's Edition in programming mode, in small checked steps.
# Without -Execute it only READS the calculator and tells what it would do.
#
#   powershell -ExecutionPolicy Bypass -File upload.ps1 -Dump my-15c-ce-dump.bin -Image ce-16cplus.bin [-Port COM6] [-Execute]
#
# Steps (every one stops the script if it fails)
#   1. Verify: the calculator's flash 0x04000-0x1FFFF equals your dump (read only). Not equal: nothing is written.
#   2. Write the image over the 15C ROM copy 2 (0xB180-0x1217F, the 15.2 mode); every page: pre-image saved from the
#      calculator first, written, read back, restored on a mismatch.
#   3. Verify the slot against the image.
#   4. Write the 4-byte pointer 0xB180 into the literal at 0x1CD10 (mode 16 then runs the 16C+); verify.
# The original 16C ROM and the factory 15 mode are not touched. Undo: undo.ps1 (writes your dump back).
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$Pointer = '',
    [string]$Port = 'COM6',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$flash = Join-Path $here 'sam_ba_flash.ps1'
if ($Pointer -eq '') { $Pointer = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $Image).Path) 'ptr16c-to-B180.bin' }
$log = Join-Path (Get-Location) ('upload-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
function Step([string]$title, [string[]]$a) {
    Write-Host ''
    Write-Host "== $title"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $flash -Port $Port @a
    if ($LASTEXITCODE -ne 0) { throw "step failed: $title" }
}
function LastLine([string]$dir) { (Get-Content (Join-Path $dir 'log.txt') | Select-Object -Last 1) }

Step '1. the calculator equals your dump (0x4000-0x1FFFF), read only' @('-Mode', 'Verify', '-Image', $Dump, '-ImageOffset', '0x4000', '-At', '0x4000', '-Length', '0x1C000', '-LogDir', "$log-1-baseline")
$l = LastLine "$log-1-baseline"
Write-Host $l
if ($l -notmatch 'VERIFY: 0 of') { throw 'the calculator does not equal your dump: nothing was written.' }
if (-not $Execute) {
    Write-Host ''
    Write-Host 'Read only run done: the calculator equals your dump. Run again with -Execute to write the image.'
    return
}
Step '2. write the image into the 15.2 slot' @('-Mode', 'Write', '-Execute', '-Image', $Image, '-At', '0xB180', '-LogDir', "$log-2-image")
Step '3. verify the slot' @('-Mode', 'Verify', '-Image', $Image, '-At', '0xB180', '-LogDir', "$log-3-verify")
if ((LastLine "$log-3-verify") -notmatch 'VERIFY: 0 of') { throw 'the slot does not equal the image' }
Step '4. write the pointer (mode 16 runs the 16C+)' @('-Mode', 'Write', '-Execute', '-Image', $Pointer, '-At', '0x1CD10', '-LogDir', "$log-4-pointer")
Step '5. verify the pointer' @('-Mode', 'Verify', '-Image', $Pointer, '-At', '0x1CD10', '-LogDir', "$log-5-verify")
if ((LastLine "$log-5-verify") -notmatch 'VERIFY: 0 of') { throw 'the pointer does not read back' }
Write-Host ''
Write-Host 'Done. Leave programming mode: press RESET on the cable, then ON. Modes: g + ENTER + ON, 4, then 7 (16) or y^x (15.2).'
