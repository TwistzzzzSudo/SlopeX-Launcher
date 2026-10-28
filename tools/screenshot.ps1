param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitMs = 7000,
    [int]$ClickRelX = -1,
    [int]$ClickRelY = -1,
    [switch]$KeepOpen
)

Add-Type -AssemblyName System.Drawing

if (-not ("WinCap" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
}
"@
}

$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds $WaitMs

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 24; $i++) {
    $proc.Refresh()
    if ($proc.HasExited) { Write-Output "PROCESS_EXITED"; exit 2 }
    $hwnd = $proc.MainWindowHandle
    if ($hwnd -ne [IntPtr]::Zero -and [WinCap]::IsWindowVisible($hwnd)) { break }
    Start-Sleep -Milliseconds 500
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Output "NO_WINDOW"
    if (-not $KeepOpen) { try { $proc.Kill() } catch {} }
    exit 3
}

[WinCap]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

if ($ClickRelX -ge 0 -and $ClickRelY -ge 0) {
    $px = $rect.Left + $ClickRelX
    $py = $rect.Top + $ClickRelY
    [WinCap]::SetCursorPos($px, $py) | Out-Null
    Start-Sleep -Milliseconds 250
    [WinCap]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    Start-Sleep -Milliseconds 60
    [WinCap]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
    Start-Sleep -Milliseconds 1200
    Write-Output "CLICKED $px,$py"
}

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "PRINTWINDOW=$ok SAVED=$Out"

if (-not $KeepOpen) {
    Start-Sleep -Milliseconds 300
    try { $proc.Kill() } catch {}
}
