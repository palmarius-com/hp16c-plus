# Guarded flash writer / verifier for the 15C CE in SAM-BA mode (ATSAM4L, 512-byte pages).
# NOTHING IS SENT TO THE CALCULATOR unless -Mode Write/Verify AND, for Write, -Execute are given.
#
# Modes
#   Plan    (default) no port opened: lists the pages and lock regions the run would touch.
#   Verify  read only (w commands): compares the device with the image slice, lists differing byte ranges.
#   Write   with -Execute only: erase + program page by page, read back, restore the pre-image on a mismatch.
#
# Image slice: image bytes [ImageOffset, ImageOffset+Length) go to device byte address At.
# Restore = Write with the original dump as image and the same range, e.g.
#   -Mode Write -Execute -Image my-dump.bin -ImageOffset 0xB180 -At 0xB180 -Length 0x7000
#
# Hard limits in code (not options):
#   * writable device range 0x4000-0x1FFFF only (lock region 0 = the bootloader is locked by hardware, and
#     0x2000-0x3FFF carries its tail and the USB descriptors: never touched);
#   * the only words ever written to the flash controller are FCMD = key 0xA5 + CPB / EP / WP for the current page;
#     no Erase All, no lock / unlock, no fuse, no security, no user page command can be built;
#   * the only other writes are page-buffer words inside the current page;
#   * pre-flight: CIDR must be 0xAB0A07E1, FSR ready, security bit clear, lock bit of every touched region clear;
#   * the pre-image of every page is read from the device and saved BEFORE the page is erased.
param(
    [ValidateSet('Plan', 'Verify', 'Write')][string]$Mode = 'Plan',
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$ImageOffset = '0x0',
    [string]$At = '0xB180',
    [string]$Length = '',
    [string]$Port = 'COM6',
    [switch]$Execute,
    [string]$LogDir = ''
)
$ErrorActionPreference = 'Stop'
function ToU32([string]$s) { return [uint32][Convert]::ToUInt32($s.Replace('0x', ''), 16) }

$imgPath = (Resolve-Path -LiteralPath $Image).Path
$img = [System.IO.File]::ReadAllBytes($imgPath)
[uint32]$imgOff = ToU32 $ImageOffset
[uint32]$at = ToU32 $At
[uint32]$len = if ($Length -ne '') { ToU32 $Length } else { [uint32]($img.Length - $imgOff) }
if ($imgOff + $len -gt $img.Length) { throw "image has only $($img.Length) bytes" }
if ($len -eq 0) { throw 'length 0' }
$MinAddr = [uint32]0x4000; $MaxEnd = [uint32]0x20000
if ($at -lt $MinAddr -or ($at + $len) -gt $MaxEnd) {
    throw ('refused: device range {0:X}-{1:X} is outside the writable range {2:X}-{3:X}' -f $at, ($at + $len - 1), $MinAddr, ($MaxEnd - 1))
}
$PageSize = 512
[int]$firstPage = [int][math]::Floor($at / $PageSize)
[int]$lastPage = [int][math]::Floor(($at + $len - 1) / $PageSize)
$regions = @($firstPage..$lastPage | ForEach-Object { [int][math]::Floor($_ / 16) } | Select-Object -Unique)

Write-Host ('Image   : {0} ({1} bytes, SHA-256 {2})' -f $imgPath, $img.Length, (Get-FileHash -Algorithm SHA256 $imgPath).Hash)
Write-Host ('Slice   : image {0:X}..{1:X} -> device {2:X}..{3:X} ({4} bytes)' -f $imgOff, ($imgOff + $len - 1), $at, ($at + $len - 1), $len)
Write-Host ('Pages   : {0}..{1} ({2} pages of 512 bytes), lock regions {3} (8 KB each)' -f $firstPage, $lastPage, ($lastPage - $firstPage + 1), ($regions -join ','))
if ($Mode -eq 'Plan' -or ($Mode -eq 'Write' -and -not $Execute)) {
    Write-Host 'PLAN ONLY: nothing was sent. (Verify reads; Write needs -Mode Write -Execute.)'
    return
}

if ($LogDir -eq '') { $LogDir = Join-Path (Get-Location) ('flash-' + $Mode.ToLower() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $LogDir | Out-Null
$logFile = Join-Path $LogDir 'log.txt'
function Log([string]$m) { Write-Host $m; Add-Content -Path $logFile -Value $m -Encoding UTF8 }
Log ("# {0} {1}  image {2}  device {3:X}..{4:X}" -f (Get-Date -Format s), $Mode, $imgPath, $at, ($at + $len - 1))

$FCMD = [uint32]0x400A0004; $FSR = [uint32]0x400A0008; $CIDR = [uint32]0x400E0740
$script:page = -1
$script:sp = New-Object System.IO.Ports.SerialPort $Port, 115200, 'None', 8, 'One'
$script:sp.ReadTimeout = 100; $script:sp.WriteTimeout = 1000

function Send-Raw([string]$cmd, [int]$waitMs) {
    # the only places where bytes leave this script; the pattern lists every command that can exist
    if ($cmd -cnotmatch '^(N#|w[0-9A-F]{1,8},#|W[0-9A-F]{1,8},[0-9A-F]{8}#)$') { throw "refused command: $cmd" }
    $script:sp.DiscardInBuffer()
    $b = [System.Text.Encoding]::ASCII.GetBytes($cmd)
    $script:sp.Write($b, 0, $b.Length)
    $rx = New-Object System.Collections.Generic.List[byte]
    $deadline = [DateTime]::UtcNow.AddMilliseconds($waitMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $rx.Add([byte]$script:sp.ReadByte()) } catch [System.TimeoutException] { if ($rx.Count -gt 0 -and $cmd[0] -ceq 'W') { break } }
        if ($cmd[0] -ceq 'w' -and $rx.Count -ge 4) { break }
    }
    return , $rx.ToArray()
}
function Read-Word([uint32]$addr) {
    $r = Send-Raw ('w{0:X},#' -f $addr) 600
    if ($r.Length -ne 4) { throw ('no 4-byte reply at {0:X8} ({1} bytes): the bootloader may need ERASE+RESET' -f $addr, $r.Length) }
    return [BitConverter]::ToUInt32($r, 0)
}
function Assert-WriteAllowed([uint32]$addr, [uint32]$val) {
    if ($addr -eq $FCMD) {
        $key = $val -shr 24; $cmd = $val -band 0x3F; $pg = ($val -shr 8) -band 0xFFFF
        if ($key -ne 0xA5) { throw 'FCMD key' }
        if (@(1, 2, 3) -notcontains [int]$cmd) { throw "refused FCMD command $cmd (only WP=1, EP=2, CPB=3 exist here)" }
        if ($pg -ne $script:page) { throw "FCMD page $pg is not the current page $($script:page)" }
        return
    }
    $lo = [uint32]($script:page * $PageSize); $hi = $lo + $PageSize - 4
    if ($script:page -ge ($MinAddr / $PageSize) -and $addr -ge $lo -and $addr -le $hi -and ($addr % 4) -eq 0) { return }
    throw ('refused write to {0:X8}' -f $addr)
}
function Write-Word([uint32]$addr, [uint32]$val) {
    Assert-WriteAllowed $addr $val
    $r = Send-Raw ('W{0:X},{1:X8}#' -f $addr, $val) 200
    return $r
}
function FcmdVal([int]$cmd, [int]$pg) { return [uint32](([uint64]0xA5 -shl 24) -bor ([uint64]$pg -shl 8) -bor [uint64]$cmd) }
function Wait-Ready {
    for ($i = 0; $i -lt 60; $i++) {
        $f = Read-Word $FSR
        if ($f -band 1) { return $f }
        Start-Sleep -Milliseconds 50
    }
    throw 'flash controller never became ready'
}
function Read-Page([int]$pg) {
    $bytes = New-Object byte[] $PageSize
    for ($k = 0; $k -lt ($PageSize / 4); $k++) {
        $w = Read-Word ([uint32]($pg * $PageSize + 4 * $k))
        [BitConverter]::GetBytes($w).CopyTo($bytes, 4 * $k)
    }
    return , $bytes
}
function Program-Page([int]$pg, [byte[]]$data) {
    $script:page = $pg
    $f = Read-Word $FSR   # a read clears PROGE / LOCKE
    $null = Write-Word $FCMD (FcmdVal 3 $pg); $null = Wait-Ready          # clear page buffer
    $null = Write-Word $FCMD (FcmdVal 2 $pg); $f = Wait-Ready               # erase page
    if ($f -band 0xC) { throw ('erase error, FSR {0:X8}' -f $f) }
    $null = Write-Word $FCMD (FcmdVal 3 $pg); $null = Wait-Ready          # clear page buffer
    for ($k = 0; $k -lt ($PageSize / 4); $k++) {                            # low word first, ascending
        $null = Write-Word ([uint32]($pg * $PageSize + 4 * $k)) ([BitConverter]::ToUInt32($data, 4 * $k))
    }
    $null = Write-Word $FCMD (FcmdVal 1 $pg); $f = Wait-Ready               # write page
    if ($f -band 0xC) { throw ('write error, FSR {0:X8}' -f $f) }
}

try {
    $script:sp.Open()
    $null = Send-Raw 'N#' 600
    $id = Read-Word $CIDR
    Log ('CIDR {0:X8}' -f $id)
    if ($id -ne [Convert]::ToUInt32('AB0A07E1', 16)) { throw 'unexpected CIDR: not this chip, stopping' }
    $f = Read-Word $FSR
    Log ('FSR  {0:X8}' -f $f)
    if (-not ($f -band 1)) { throw 'flash controller not ready' }
    if ($f -band 0x10) { throw 'security bit set' }
    foreach ($rg in $regions) { if (($f -shr (16 + $rg)) -band 1) { throw "lock region $rg is locked: stopping" } }

    if ($Mode -eq 'Verify') {
        $bad = New-Object System.Collections.Generic.List[string]; $nbad = 0
        for ($off = 0; $off -lt $len; $off += 4) {
            $w = Read-Word ([uint32]($at + $off))
            $exp = [BitConverter]::ToUInt32($img, [int]($imgOff + $off))
            if ($w -ne $exp) { $nbad++; if ($bad.Count -lt 40) { $bad.Add(('{0:X6}: device {1:X8} image {2:X8}' -f ($at + $off), $w, $exp)) } }
        }
        $bad | ForEach-Object { Log $_ }
        Log ("VERIFY: {0} of {1} words differ" -f $nbad, ($len / 4))
        return
    }

    # Write
    for ($pg = $firstPage; $pg -le $lastPage; $pg++) {
        $pre = Read-Page $pg
        [System.IO.File]::WriteAllBytes((Join-Path $LogDir ('preimage-page{0:D3}-{1:X5}.bin' -f $pg, ($pg * $PageSize))), $pre)
        $new = [byte[]]$pre.Clone()
        for ($i = 0; $i -lt $PageSize; $i++) {
            $a = [uint32]($pg * $PageSize + $i)
            if ($a -ge $at -and $a -lt ($at + $len)) { $new[$i] = $img[$imgOff + ($a - $at)] }
        }
        if ([System.Linq.Enumerable]::SequenceEqual($pre, $new)) { Log ("page {0}: already equal, skipped" -f $pg); continue }
        Log ("page {0}: pre-image saved, programming" -f $pg)
        try {
            Program-Page $pg $new
            $back = Read-Page $pg
            if (-not [System.Linq.Enumerable]::SequenceEqual($back, $new)) { throw 'read-back differs' }
            Log ("page {0}: read back equal" -f $pg)
        } catch {
            Log ("page {0}: FAILED ({1}); restoring the pre-image once" -f $pg, $_.Exception.Message)
            Program-Page $pg $pre
            $back = Read-Page $pg
            if ([System.Linq.Enumerable]::SequenceEqual($back, $pre)) { Log ("page {0}: pre-image restored and verified" -f $pg) }
            else { Log ("page {0}: RESTORE FAILED. Keep power and USB, run the restore command with the dump (see README)." -f $pg) }
            throw
        }
    }
    Log 'WRITE: all pages programmed and read back equal. Run -Mode Verify next.'
}
finally {
    if ($script:sp.IsOpen) { $script:sp.Close() }
}
