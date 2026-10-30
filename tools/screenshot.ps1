param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitMs = 7000,
    [int]$ClickRelX = -1,
    [int]$ClickRelY = -1,
    [string]$Page = "",
    [string]$ClickText = "",
    [int]$Scroll = 0,
    [int]$PostWaitMs = 1200,
    [int]$PostClickWaitMs = -1,
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
    public static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
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

# -Page: select a nav item by its name via UI Automation (DPI independent)
if ($Page -ne "") {
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $aeRoot = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $aeCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $aeItems = $aeRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $aeCond)
        $aePat = $null
        foreach ($it in $aeItems) {
            # WPF ListBoxItem has no Name of its own; collect text from its descendants
            $kids = $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $label = ""
            foreach ($k in $kids) {
                if ($k.Current.Name) { $label = $label + $k.Current.Name }
            }
            if ($label -like "*$Page*") {
                $aePat = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                Write-Output ("PAGE_LABEL=" + $label)
                break
            }
        }
        if ($null -ne $aePat) {
            $aePat.Select()
            Start-Sleep -Milliseconds $PostWaitMs
            Write-Output "PAGE_SELECTED"
        }
        else {
            Write-Output "PAGE_NOT_FOUND"
        }
    }
    catch {
        Write-Output ("PAGE_ERR " + $_.Exception.Message)
    }
}

# -ClickText: click targets by label, pipe separated for a sequence (e.g. "open|confirm")
# Matches Button and RadioButton (dialog option rows are radio buttons).
if ($ClickText -ne "") {
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes

        foreach ($target in ($ClickText -split '\|')) {
            if ($target -eq "") { continue }

            $aeRoot2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $condBtn = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $condRadio = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            $condAny = New-Object System.Windows.Automation.OrCondition($condBtn, $condRadio)

            $clicked = $false
            foreach ($b in $aeRoot2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condAny)) {
                $kids = $b.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)
                $lab = ""
                foreach ($k in $kids) { if ($k.Current.Name) { $lab = $lab + $k.Current.Name } }
                if (-not $lab) { $lab = $b.Current.Name }
                if ($lab -like "*$target*") {
                    $r = $b.Current.BoundingRectangle
                    $cx = [int]($r.X + $r.Width / 2)
                    $cy = [int]($r.Y + $r.Height / 2)
                    [WinCap]::SetCursorPos($cx, $cy) | Out-Null
                    Start-Sleep -Milliseconds 200
                    [WinCap]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                    Start-Sleep -Milliseconds 60
                    [WinCap]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                    Write-Output ("CLICKED=" + $lab)
                    $clicked = $true
                    break
                }
            }
            if (-not $clicked) { Write-Output ("CLICK_TARGET_NOT_FOUND=" + $target) }
            $after = if ($PostClickWaitMs -ge 0) { $PostClickWaitMs } else { $PostWaitMs }
            Start-Sleep -Milliseconds $after
        }
    }
    catch {
        Write-Output ("CLICK_ERR " + $_.Exception.Message)
    }
}

$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Output ("SIZE=" + $w + "x" + $h)
if ($w -le 0 -or $h -le 0) {
    Write-Output "BAD_RECT"
    if (-not $KeepOpen) { try { $proc.Kill() } catch { } }
    exit 4
}

# -Scroll: wheel scrolling over the page (positive = down)
# Keep the cursor over the page's left gutter: if it sits over an inner list,
# the wheel scrolls that list instead of the page.
if ($Scroll -ne 0) {
    [WinCap]::SetCursorPos(($rect.Left + [int]($w * 0.185)), ($rect.Top + [int]($h * 0.45))) | Out-Null
    Start-Sleep -Milliseconds 200
    $delta = if ($Scroll -gt 0) { -120 } else { 120 }
    for ($i = 0; $i -lt [Math]::Abs($Scroll); $i++) {
        [WinCap]::mouse_event(0x0800, 0, 0, $delta, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
    Write-Output ("SCROLLED=" + $Scroll)
    Start-Sleep -Milliseconds 500
}

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
