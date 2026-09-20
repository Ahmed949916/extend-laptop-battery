<#
.SYNOPSIS
    Capture the PowerDial window, one PNG per page, into docs\.

.DESCRIPTION
    Screenshots for the README. It captures the app's own window rather than the whole
    screen, so the shots are consistently sized and carry no desktop around them.

    It cannot drive the app, so it prompts: switch PowerDial to the page it names, press
    Enter, and it captures that page. Six prompts, about a minute.

    Window bounds come from DwmGetWindowAttribute rather than GetWindowRect. On Windows 10
    and 11 a window's real rectangle includes several pixels of invisible resize border,
    so GetWindowRect shots come out with a dark margin down each side.

.PARAMETER OutDir
    Where to write them. Defaults to docs\ beside this script's repo.

.PARAMETER Delay
    Seconds to wait after focusing the window before capturing, so any hover or focus
    state from the click has settled. Default 1.

.EXAMPLE
    .\scripts\shots.ps1
#>

[CmdletBinding()]
param(
    [string] $OutDir,
    [int] $Delay = 1
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'docs' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing

if (-not ('PdShot.Win' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace PdShot
{
    public static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        // 9 = DWMWA_EXTENDED_FRAME_BOUNDS: the window as drawn, without the invisible
        // resize border GetWindowRect includes.
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    }
}
'@
}

function Get-PowerDialWindow {
    $p = Get-Process PowerDial -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } |
         Select-Object -First 1
    if (-not $p) { return [IntPtr]::Zero }
    return $p.MainWindowHandle
}

function Save-WindowShot($hWnd, $path) {
    [void][PdShot.Win]::ShowWindow($hWnd, 9)          # SW_RESTORE
    [void][PdShot.Win]::SetForegroundWindow($hWnd)
    Start-Sleep -Seconds $Delay

    $r = New-Object PdShot.Win+RECT
    $size = [Runtime.InteropServices.Marshal]::SizeOf([type]([PdShot.Win+RECT]))
    if ([PdShot.Win]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, $size) -ne 0) {
        [void][PdShot.Win]::GetWindowRect($hWnd, [ref]$r)
    }

    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "PowerDial's window has no size - is it minimised?" }

    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object Drawing.Size $w, $h))
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()

    $kb = (Get-Item $path).Length / 1KB
    Write-Host ("  saved {0}   {1} x {2}   {3:n0} KB" -f (Split-Path $path -Leaf), $w, $h, $kb) -ForegroundColor Green
}

$pages = @(
    @{ File = 'overview.png';    Page = 'Overview' }
    @{ File = 'power-modes.png'; Page = 'Power modes' }
    @{ File = 'insights.png';    Page = 'Insights' }
    @{ File = 'advanced.png';    Page = 'Advanced' }
    @{ File = 'battery-health.png'; Page = 'Battery health' }
    @{ File = 'diagnostics.png'; Page = 'Diagnostics' }
)

$hWnd = Get-PowerDialWindow
if ($hWnd -eq [IntPtr]::Zero) {
    Write-Host "PowerDial is not running, or its window is hidden in the tray." -ForegroundColor Red
    Write-Host "Start it (or double-click the tray icon to show it) and run this again."
    exit 1
}

Write-Host ""
Write-Host "Capturing the PowerDial window into $OutDir" -ForegroundColor Cyan
Write-Host "Maximise it first if you want wide shots - whatever size it is, is what you get."
Write-Host ""

foreach ($p in $pages) {
    Read-Host "Click '$($p.Page)' in the sidebar, then press Enter"
    $hWnd = Get-PowerDialWindow
    if ($hWnd -eq [IntPtr]::Zero) { Write-Host "  window gone - skipped" -ForegroundColor Yellow; continue }
    Save-WindowShot $hWnd (Join-Path $OutDir $p.File)
}

Write-Host ""
Write-Host "Done. $OutDir" -ForegroundColor Green
Get-ChildItem $OutDir -Filter *.png | Select-Object Name, @{n='KB';e={[int]($_.Length/1KB)}}, LastWriteTime | Format-Table -AutoSize
