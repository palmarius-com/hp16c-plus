# Read-only SAM-BA monitor reader (15C CE in programming mode).
# Commands this script can build, and no others:
#   N#                 switch the monitor to ASCII mode (handshake, no memory access)
#   w<hexaddr>,#       read ONE 32-bit word (lowercase w = read; uppercase W would write)
# There is no code path that creates W, S, G, O, H or any erase/lock command.
# Every command and every reply byte goes to the log. Stops at the first word
# that gets no answer (a bus fault may hang the bootloader): never skips on.
# Usage: powershell -File tools\sam_ba_read.ps1 -Start 0x0 -Words 4 [-Port COM6]
#        [-Out file.txt] [-Ascii]   (-Ascii sends N# first)
param(
    [string]$Port = 'COM6',
    [string]$Start = '0x0',
    [int]$Words = 4,
    [switch]$Ascii,
    [string]$Out = '',
    [int]$ReplyMs = 600,
    [string[]]$At = @(),
    [string]$BinOut = '',
    [switch]$Quiet,
    [switch]$AnyAddress   # skip the allow-list below: still read commands only; an unmapped address can hang the bootloader
)
$ErrorActionPreference = 'Stop'
[uint32]$firstAddr = [Convert]::ToUInt32($Start.Replace('0x', ''), 16)
if (($firstAddr % 4) -ne 0) { throw 'Start must be a multiple of 4' }

function Send-Cmd([System.IO.Ports.SerialPort]$sp, [string]$cmd) {
    if ($cmd -cnotmatch '^(N#|w[0-9A-F]{1,8},#)$') { throw "refused command: $cmd" }
    $sp.DiscardInBuffer()
    $b = [System.Text.Encoding]::ASCII.GetBytes($cmd)
    $sp.Write($b, 0, $b.Length)
    $rx = New-Object System.Collections.Generic.List[byte]
    $deadline = [DateTime]::UtcNow.AddMilliseconds($ReplyMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $rx.Add([byte]$sp.ReadByte()) } catch [System.TimeoutException] { }
        # a 'w' read answers with exactly 4 raw bytes: stop waiting then
        if ($cmd.StartsWith('w') -and $rx.Count -ge 4) { break }
    }
    return ,$rx.ToArray()
}

$log = New-Object System.Collections.Generic.List[string]
$bin = New-Object System.Collections.Generic.List[byte]
$t0 = [DateTime]::UtcNow
$sp = New-Object System.IO.Ports.SerialPort $Port, 115200, 'None', 8, 'One'
$sp.ReadTimeout = 100
$sp.WriteTimeout = 1000
try {
    $sp.Open()
    if ($Ascii) {
        $r = Send-Cmd $sp 'N#'
        $line = "N#  -> " + (($r | ForEach-Object { '{0:X2}' -f $_ }) -join ' ') + "  '" +
            [System.Text.Encoding]::ASCII.GetString($r).Replace("`r", '\r').Replace("`n", '\n') + "'"
        Write-Host $line; $log.Add($line)
    }
    # -At 0x4000,0x8000 reads exactly those words (single words); else -Start/-Words
    $addrList = @()
    if ($At.Count -gt 0) {
        foreach ($a in ($At -join ',').Split(',')) { $addrList += [Convert]::ToUInt32(([string]$a).Replace('0x', ''), 16) }
    } else {
        for ($k = 0; $k -lt $Words; $k++) { $addrList += [uint32]($firstAddr + 4 * $k) }
    }
    foreach ($addr in $addrList) {
        if (($addr % 4) -ne 0) { throw ('address not word aligned: {0:X}' -f $addr) }
        # only ranges that answered on the 15C CE (2026-11-06). 0x2000FFFC did not answer
        # and stopped the bootloader: anything else is refused, not tried.
        $ok = ($addr -le 0x3FFFC) -or ($addr -ge 0x800000 -and $addr -le 0x8001FC) -or
              ($addr -ge 0x20000000 -and $addr -le 0x20007FFC) -or ($addr -ge 0x400E0740 -and $addr -le 0x400E0744) -or
              ($addr -ge 0x400A0000 -and $addr -le 0x400A001C) -or ($addr -eq [uint32]3758157056)
        if (-not $ok -and -not $AnyAddress) { throw ('address outside the known-safe ranges: {0:X8}' -f $addr) }
        $cmd = 'w{0:X},#' -f $addr
        $r = Send-Cmd $sp $cmd
        $wordText = '--------'
        if ($r.Length -eq 4) { $wordText = '{0:X8}' -f [BitConverter]::ToUInt32($r, 0) }
        $line = ('{0:X8}  {1,-14} -> {2,2} bytes  raw: {3}  word(LE): {4}' -f $addr, $cmd, $r.Length,
            (($r | ForEach-Object { '{0:X2}' -f $_ }) -join ' '), $wordText)
        if (-not $Quiet) { Write-Host $line }
        $log.Add($line)
        if ($r.Length -eq 4) { $bin.AddRange($r) }
        if ($r.Length -ne 4 -and $r.Length -ne 0) { $m = 'STOP: reply is not 4 bytes.'; Write-Host $m; $log.Add($m); break }
        if ($r.Length -eq 0) { $m = 'STOP: no reply, not reading further.'; Write-Host $m; $log.Add($m); break }
    }
}
finally {
    if ($sp.IsOpen) { $sp.Close() }
    Write-Host ('{0} bytes read in {1:N1} s' -f $bin.Count, ([DateTime]::UtcNow - $t0).TotalSeconds)
    if ($BinOut -ne '') { [System.IO.File]::WriteAllBytes($BinOut, $bin.ToArray()) }
    if ($Out -ne '') { $log | Set-Content -Encoding UTF8 -Path $Out }
}
