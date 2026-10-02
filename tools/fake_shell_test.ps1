# ============================================================================
#  Fake-shell harness: does the pet yield to shell UI?
#
#  Creates real windows using the REAL shell class names (a class name is only
#  unique per module, so registering "Shell_TrayWnd" here does not clash with the
#  system one) and stacks them the way the shell does: taskbar below, tray
#  overflow flyout above it. Both overlap the pet, so a pet that is above either
#  of them is covering something the user needs to click.
#
#  Deterministic on purpose: the real auto-hide taskbar needs a mouse hover and a
#  tray flyout needs a click on a chevron, neither of which belongs in a test.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File tools\fake_shell_test.ps1
# ============================================================================
[CmdletBinding()]
param(
    [int]$HoldSeconds = 3
)

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class FakeShell {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct WNDCLASSEX {
    public uint cbSize; public uint style; public IntPtr lpfnWndProc;
    public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
    public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    public IntPtr hIconSm;
  }
  public delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEX c);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
  [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public struct RECT { public int L,T,R,B; }
}
"@

$script:wndProc = [FakeShell+WndProcD]{
    param($h, $m, $w, $l)
    # Swallow paint messages: this window is never painted and would otherwise
    # spin on WM_PAINT.
    if ($m -eq 0x000F -or $m -eq 0x0014) { return [IntPtr]::Zero }
    return [FakeShell]::DefWindowProcW($h, $m, $w, $l)
}
$procPtr = [System.Runtime.InteropServices.Marshal]::GetFunctionPointerForDelegate($script:wndProc)
$hInst = [FakeShell]::GetModuleHandleW($null)
$stylePopupVisible = [uint32]2415919104      # WS_POPUP | WS_VISIBLE
$exTopmostTool     = [uint32]136             # WS_EX_TOPMOST | WS_EX_TOOLWINDOW

function New-FakeShell([string]$cls, [int]$x, [int]$y, [int]$w, [int]$h) {
    $wc = New-Object FakeShell+WNDCLASSEX
    $wc.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][FakeShell+WNDCLASSEX])
    $wc.lpfnWndProc = $procPtr
    $wc.hInstance = $hInst
    $wc.lpszClassName = $cls
    [void][FakeShell]::RegisterClassExW([ref]$wc)
    $hwnd = [FakeShell]::CreateWindowExW($exTopmostTool, $cls, "fake " + $cls, $stylePopupVisible,
                                         $x, $y, $w, $h, [IntPtr]::Zero, [IntPtr]::Zero, $hInst, [IntPtr]::Zero)
    if ($hwnd -eq [IntPtr]::Zero) { throw "CreateWindowEx failed for $cls" }
    return $hwnd
}

function Get-PetWindow {
    $procId = (Get-CimInstance Win32_Process -Filter "Name='DshPet.exe'" | Select-Object -First 1).ProcessId
    if (-not $procId) { throw "the widget is not running" }
    $script:pet = [IntPtr]::Zero
    $cb = [FakeShell+EnumProc]{
        param($h, $p)
        $w = 0; [void][FakeShell]::GetWindowThreadProcessId($h, [ref]$w)
        if ($w -eq $procId) {
            $ex = [int64][FakeShell]::GetWindowLongPtr($h, -20)
            if (($ex -band 0x80000) -ne 0 -and ($ex -band 0x8) -ne 0) { $script:pet = $h }
        }
        return $true
    }
    [void][FakeShell]::EnumWindows($cb, [IntPtr]::Zero)
    if ($script:pet -eq [IntPtr]::Zero) { throw "could not find the pet window" }
    return $script:pet
}

function Test-OtherAbovePet([IntPtr]$pet, [IntPtr]$other) {
    $cur = [FakeShell]::GetWindow($pet, 3); $n = 0
    while ($cur -ne [IntPtr]::Zero -and $n -lt 600) {
        if ($cur -eq $other) { return $true }
        $cur = [FakeShell]::GetWindow($cur, 3); $n++
    }
    return $false
}

# How deep the top-level z-order chain actually is - the pet walks it every tick,
# so a chain far longer than the number of real windows would point at a cycle.
function Measure-ChainDepth {
    $cur = [FakeShell]::GetTopWindow([IntPtr]::Zero)
    if ($cur -eq [IntPtr]::Zero) { $cur = [FakeShell]::GetWindow([FakeShell]::GetDesktopWindow(), 5) }
    $start = $cur; $n = 0
    while ($cur -ne [IntPtr]::Zero -and $n -lt 2000) {
        $cur = [FakeShell]::GetWindow($cur, 2); $n++
        if ($cur -eq $start) { return "$n (CYCLE back to the start)" }
    }
    return "$n"
}

$pet = Get-PetWindow
$r = New-Object FakeShell+RECT
[void][FakeShell]::GetWindowRect($pet, [ref]$r)
Write-Host "pet 0x$('{0:X}' -f [int64]$pet) at $($r.L),$($r.T) $($r.R-$r.L)x$($r.B-$r.T)" -ForegroundColor Cyan
Write-Host "top-level z-order chain depth: $(Measure-ChainDepth)"

$logBefore = (Get-Content 'pet.log').Count

# Stack them the way the shell does: taskbar first (lower), flyout second (above).
$bar    = New-FakeShell 'Shell_TrayWnd' 0 1392 2560 48
Start-Sleep -Milliseconds 600
$flyout = New-FakeShell 'TopLevelWindowForOverflowXamlIsland' 2148 1118 234 274
Start-Sleep -Seconds $HoldSeconds

Write-Host "fake taskbar 0x$('{0:X}' -f [int64]$bar)   visible=$([FakeShell]::IsWindowVisible($bar))"
Write-Host "fake flyout  0x$('{0:X}' -f [int64]$flyout)   visible=$([FakeShell]::IsWindowVisible($flyout))"

$barAbove    = Test-OtherAbovePet $pet $bar
$flyoutAbove = Test-OtherAbovePet $pet $flyout
Write-Host ""
Write-Host "taskbar ABOVE pet : $barAbove    (want True  - the pet must not cover it)"
Write-Host "flyout  ABOVE pet : $flyoutAbove (want True  - the pet must not cover it)" -ForegroundColor $(if ($barAbove -and $flyoutAbove) { 'Green' } else { 'Red' })

Write-Host "`n-- widget log since the fakes appeared --"
$all = Get-Content 'pet.log'
$all[$logBefore..($all.Count - 1)] | Where-Object { $_ -match 'zorder:|stepped aside|back in front|re-asserted' } |
    ForEach-Object { Write-Host "  $_" }

[void][FakeShell]::DestroyWindow($flyout)
[void][FakeShell]::DestroyWindow($bar)