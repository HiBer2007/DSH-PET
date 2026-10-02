# Scratch window used to work out what a desktop capturer can actually see.
#
# Two shapes, both mimicking the pet as it would look after the ownership fix:
#   plain     - no WS_EX_LAYERED at all: an ordinary opaque window
#   colorkey  - WS_EX_LAYERED + LWA_COLORKEY: the key colour is see-through on
#               screen, and the window still has a normal redirection surface
# Runs for -Seconds then exits.
param(
    [ValidateSet('plain', 'colorkey')][string]$Variant = 'colorkey',
    [string]$Title = 'CapProbe',
    [int]$Seconds = 60
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SW {
  [DllImport("user32.dll")] public static extern int SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
  public const int GWL_EXSTYLE = -20;
  public const long WS_EX_LAYERED = 0x00080000;
  public const long WS_EX_TOOLWINDOW = 0x00000080;
  public const long WS_EX_NOACTIVATE = 0x08000000;
  public const long WS_EX_TOPMOST = 0x00000008;
  public static void Style(IntPtr h, long add) {
    long ex = (long)GetWindowLongPtrW(h, GWL_EXSTYLE) | add;
    SetWindowLongPtrW(h, GWL_EXSTYLE, (IntPtr)ex);
    SetWindowPos(h, IntPtr.Zero, 0,0,0,0, 0x0001|0x0002|0x0004|0x0020);
  }
  public static void ClearOwner(IntPtr h) {
    SetWindowLongPtrW(h, -8, IntPtr.Zero);
    SetWindowPos(h, IntPtr.Zero, 0,0,0,0, 0x0001|0x0002|0x0004|0x0020);
  }
}
"@

$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(120, 120)
$form.ClientSize = New-Object System.Drawing.Size(300, 300)
$form.TopMost = $true
$form.ShowInTaskbar = $true          # no hidden owner window; the fix under test
$form.Add_Paint({
    param($s, $e)
    $e.Graphics.Clear([System.Drawing.Color]::Magenta)
    $e.Graphics.FillEllipse([System.Drawing.Brushes]::White, 60, 60, 180, 180)
})
$form.Add_Shown({
    $h = $form.Handle
    [SW]::Style($h, [SW]::WS_EX_TOOLWINDOW -bor [SW]::WS_EX_NOACTIVATE -bor [SW]::WS_EX_TOPMOST)
    [SW]::ClearOwner($h)
    if ($Variant -eq 'colorkey') {
        [SW]::Style($h, [SW]::WS_EX_LAYERED)
        $ok = [SW]::SetLayeredWindowAttributes($h, 0x00FF00FF, 0, 1)
        Write-Host "colorkey set: $ok"
    }
    Write-Host ("window ready: '{0}' {1} hwnd=0x{2:X} ex=0x{3:X}" -f $form.Text, $Variant, [long]$h, [long][SW]::GetWindowLongPtrW($h, -20))
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $Seconds * 1000
$timer.Add_Tick({ $form.Close() })
$timer.Start()

[System.Windows.Forms.Application]::Run($form)
Write-Host 'scratch window closed'
