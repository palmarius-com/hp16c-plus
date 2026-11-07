# Puts the 15C CE back to what your dump says: writes the original 15C ROM copy 2 (0xB180-0x1217F) and the original
# pointer literal (0x1CD10-0x1CD13) from your dump, then verifies both. Needs the calculator in programming mode.
#
#   powershell -ExecutionPolicy Bypass -File undo.ps1 -Dump my-15c-ce-dump.bin [-Port COM6] [-Execute]
#
# Without -Execute it only reads and says what it would do.
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [string]$Port = 'COM6',
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$flash = Join-Path $here 'sam_ba_flash.ps1'
$log = Join-Path (Get-Location) ('undo-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
function Step([string]$title, [string[]]$a) {
    Write-Host ''
    Write-Host "== $title"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $flash -Port $Port @a
    if ($LASTEXITCODE -ne 0) { throw "step failed: $title" }
}
if (-not $Execute) {
    Step 'what differs between the calculator and your dump (slot 0xB180-0x1217F), read only' @('-Mode', 'Verify', '-Image', $Dump, '-ImageOffset', '0xB180', '-At', '0xB180', '-Length', '0x7000', '-LogDir', "$log-check")
    Write-Host 'Read only run done. Run again with -Execute to write your dump back.'
    return
}
Step 'write the original slot back' @('-Mode', 'Write', '-Execute', '-Image', $Dump, '-ImageOffset', '0xB180', '-At', '0xB180', '-Length', '0x7000', '-LogDir', "$log-slot")
Step 'write the original pointer back' @('-Mode', 'Write', '-Execute', '-Image', $Dump, '-ImageOffset', '0x1CD10', '-At', '0x1CD10', '-Length', '4', '-LogDir', "$log-pointer")
Step 'verify the slot' @('-Mode', 'Verify', '-Image', $Dump, '-ImageOffset', '0xB180', '-At', '0xB180', '-Length', '0x7000', '-LogDir', "$log-vslot")
Step 'verify the pointer' @('-Mode', 'Verify', '-Image', $Dump, '-ImageOffset', '0x1CD10', '-At', '0x1CD10', '-Length', '4', '-LogDir', "$log-vptr")
Write-Host ''
Write-Host 'Done. Leave programming mode: press RESET on the cable, then ON.'
