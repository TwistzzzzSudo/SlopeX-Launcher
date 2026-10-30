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
    [switch]$KeepOpen,
    [switch]$FullScreen
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

            # 序列里可以夹带非点击动作：
            #   type:文字   → 往当前窗口的输入框里填值（UIA ValuePattern，不依赖键盘焦点）
            #   wait:毫秒   → 停顿
            if ($target -like 'wait:*') {
                [int]$ms = ($target.Substring(5))
                Start-Sleep -Milliseconds $ms
                continue
            }

            if ($target -like 'hover:*') {
                [WinCap]::SetForegroundWindow($hwnd) | Out-Null
                Start-Sleep -Milliseconds 250
                $xy = ($target.Substring(6)) -split ','
                $hr = New-Object WinCap+RECT
                [WinCap]::GetWindowRect($hwnd, [ref]$hr) | Out-Null
                $hx = [int]$hr.Left + [int]$xy[0]
                $hy = [int]$hr.Top + [int]$xy[1]
                [WinCap]::SetCursorPos($hx, $hy) | Out-Null
                Start-Sleep -Milliseconds 450
                Write-Output ("HOVER=" + $xy[0] + "," + $xy[1])
                continue
            }

            if ($target -like 'type:*') {
                $text = $target.Substring(5)
                $aeT = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
                $condEdit = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Edit)
                $edits = $aeT.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condEdit)
                $vp = $null
                $ok = $false
                foreach ($ed in $edits) {
                    if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                        $vp.SetValue($text); $ok = $true; break
                    }
                }
                Write-Output ("TYPE=" + $text + " ok=" + $ok)
                continue
            }



            $aeRoot2 = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $condBtn = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $condRadio = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            $condAny = New-Object System.Windows.Automation.OrCondition($condBtn, $condRadio)

            $clicked = $false
            for ($attempt = 0; $attempt -lt 6 -and -not $clicked; $attempt++) {
                if ($attempt -gt 0) { Start-Sleep -Milliseconds 1500 }

                # 两遍匹配：先找标签完全相等的，再退回"包含"。
                # 只做包含匹配会把「安装」错配到「安装新版本」，自动化里很危险。
                $pick = $null
                $pickLab = ""
                foreach ($b in $aeRoot2.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condAny)) {
                    $kids = $b.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition)
                    $lab = ""
                    foreach ($k in $kids) { if ($k.Current.Name) { $lab = $lab + $k.Current.Name } }
                    if (-not $lab) { $lab = $b.Current.Name }

                    if ($lab -eq $target) { $pick = $b; $pickLab = $lab; break }
                    if ($null -eq $pick -and $lab -like "*$target*") { $pick = $b; $pickLab = $lab }
                }

                if ($null -eq $pick) { continue }

                $b = $pick
                $lab = $pickLab

                # 点击前重新置前：其他窗口可能在这期间抢走焦点
                [WinCap]::SetForegroundWindow($hwnd) | Out-Null
                Start-Sleep -Milliseconds 120

                # 优先 UIA Invoke / Select：不依赖窗口层级，被遮挡也能点中
                $pattern = $null
                $did = $false
                try {
                    if ($b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                        $pattern.Invoke(); $did = $true
                    }
                    elseif ($b.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                        $pattern.Select(); $did = $true
                    }
                } catch { $did = $false }

                if (-not $did) {
                    $r = $b.Current.BoundingRectangle
                    $cx = [int]($r.X + $r.Width / 2)
                    $cy = [int]($r.Y + $r.Height / 2)
                    [WinCap]::SetCursorPos($cx, $cy) | Out-Null
                    Start-Sleep -Milliseconds 200
                    [WinCap]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                    Start-Sleep -Milliseconds 60
                    [WinCap]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                }

                Write-Output ("CLICKED=" + $lab)
                $clicked = $true
            }   # attempt 循环
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

if ($FullScreen) {
    Add-Type -AssemblyName System.Windows.Forms
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $g.Dispose()
    $ok = $true
}
else {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "PRINTWINDOW=$ok SAVED=$Out"

if (-not $KeepOpen) {
    Start-Sleep -Milliseconds 300
    try { $proc.Kill() } catch {}
}
