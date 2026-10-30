# Start the simulator, optionally type keys, save a screenshot of its window.
# Windows only (user32 + System.Drawing). Keys use SendKeys syntax, separated
# by '|' (each group is sent, then a short pause). Typed keys go to whatever
# window has the focus: do not touch the desktop while it runs.
#
#   .\screenshot.ps1 -AppArgs '--model 15c' -Keys '{ESC}|1|2{ENTER}3{+}' -Out 15c-add
#   .\screenshot.ps1 -Exe path\to\VoyagerSim.exe -Out start-page
param(
    [string]$AppArgs = "",
    [string]$Keys = "",
    [string]$Out = "screenshot",
    [string]$Exe = "",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\docs\screenshots")
)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ShotWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct R { public int L, T, Rt, B; }
}
'@
[ShotWin]::SetProcessDPIAware() | Out-Null
if (-not $Exe) {
    $Exe = (Get-ChildItem -Recurse (Join-Path $PSScriptRoot "..\src\Hp16c.App\bin") -Filter 'VoyagerSim.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Exe) { throw "VoyagerSim.exe not found: build csharp/src/Hp16c.App first or pass -Exe" }
$p = if ($AppArgs) { Start-Process $Exe -ArgumentList $AppArgs -PassThru } else { Start-Process $Exe -PassThru }
Start-Sleep 5
$p.Refresh(); $h = $p.MainWindowHandle
[ShotWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 600
foreach ($k in ($Keys -split '\|')) {
    if ($k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }
}
$r = New-Object ShotWin+R; [ShotWin]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$small = New-Object System.Drawing.Bitmap $bmp, ([int]($bmp.Width / 2)), ([int]($bmp.Height / 2))
New-Item -ItemType Directory -Force $OutDir | Out-Null
$file = Join-Path $OutDir "$Out.png"
$small.Save($file)
Stop-Process -Id $p.Id -Force
"saved $file"
