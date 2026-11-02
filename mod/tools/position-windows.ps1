<#
.SYNOPSIS
  Pins two X4 windows to the left and right edges of the desktop and keeps them there.
.DESCRIPTION
  Built for the two-instances-on-one-PC test stand (docs/two-instances-one-pc.md): one X4
  window owns the left half of the screen, the second one the right half, with no gap and no
  overlap at the seam.

  The alignment works on the CLIENT area, not on the window rectangle. An X4 window at
  1280x720 is 1296x759: an 8 px border on each side and a 31 px title bar. Aligning window
  rectangles therefore leaves an 8 px strip of desktop visible at the outer edges.

  The window is only MOVED, never resized: X4 restores its own window size on menu and save
  transitions, so SetWindowPos with a new cx/cy does not stick. Because the game also resets
  its own position, the script re-applies the offsets on a timer for as long as it runs.

  Both windows are also pinned to the top edge of the virtual screen (title bar above the
  screen corner, render area starting at y=0).
.PARAMETER LeftProcess
  Process name of the instance that goes to the left edge. Default: X4.
.PARAMETER RightProcess
  Process name of the instance that goes to the right edge. Default: X4b (the patched second
  instance; see docs/two-instances-one-pc.md).
.PARAMETER Seconds
  How long to keep correcting positions. 0 (default) runs until the process is stopped.
.PARAMETER Poll
  Seconds between checks. Default: 2.
.EXAMPLE
  .\position-windows.ps1
  Start correcting forever with the default names (X4 left, X4b right).
.EXAMPLE
  .\position-windows.ps1 -LeftProcess X4 -RightProcess X4b -Seconds 300 -Poll 1
  Correct for five minutes, once per second.
#>
[CmdletBinding()]
param(
  [string]$LeftProcess = 'X4',
  [string]$RightProcess = 'X4b',
  [int]$Seconds = 0,
  [int]$Poll = 2
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class X4WinPos {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
"@

$SWP_NOSIZE = 0x0001
$SWP_NOZORDER = 0x0004
$SWP_NOACTIVATE = 0x0010
$flags = $SWP_NOSIZE -bor $SWP_NOZORDER -bor $SWP_NOACTIVATE

$vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
$leftEdge = $vs.Left
$rightEdge = $vs.Left + $vs.Width
$topEdge = $vs.Top
Write-Host "virtual screen: $($vs.Left),$($vs.Top) $($vs.Width)x$($vs.Height); left=$LeftProcess right=$RightProcess"

$deadline = if ($Seconds -gt 0) { (Get-Date).AddSeconds($Seconds) } else { [datetime]::MaxValue }
while ((Get-Date) -lt $deadline) {
    foreach ($item in @(@{ name = $LeftProcess; left = $true }, @{ name = $RightProcess; left = $false })) {
        $p = Get-Process -Name $item.name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $h = $p.MainWindowHandle

        $wr = New-Object X4WinPos+RECT
        if (-not [X4WinPos]::GetWindowRect($h, [ref]$wr)) { continue }
        $cr = New-Object X4WinPos+RECT
        if (-not [X4WinPos]::GetClientRect($h, [ref]$cr)) { continue }
        $pt = New-Object X4WinPos+POINT
        $pt.X = 0; $pt.Y = 0
        if (-not [X4WinPos]::ClientToScreen($h, [ref]$pt)) { continue }

        $clientW = $cr.Right - $cr.Left
        $clientH = $cr.Bottom - $cr.Top
        if ($clientW -le 0 -or $clientH -le 0) { continue }

        # frameL = the part of the window frame left of the render area (8 px on X4 at 1280x720)
        $frameL = $pt.X - $wr.Left
        $frameT = $pt.Y - $wr.Top
        $targetX = if ($item.left) { $leftEdge - $frameL } else { $rightEdge - $clientW - $frameL }
        $targetY = $topEdge - $frameT

        if ([Math]::Abs($wr.Left - $targetX) -ge 2 -or [Math]::Abs($wr.Top - $targetY) -ge 2) {
            [void][X4WinPos]::SetWindowPos($h, [IntPtr]::Zero, $targetX, $targetY, 0, 0, $flags)
            Write-Host ("{0} pid={1} window {2},{3} -> {4},{5} (client {6}..{7} x {8}..{9})" -f $item.name, $p.Id, $wr.Left, $wr.Top, $targetX, $targetY, ($targetX + $frameL), ($targetX + $frameL + $clientW), ($targetY + $frameT), ($targetY + $frameT + $clientH))
        }
    }
    Start-Sleep -Seconds $Poll
}
