# Diagnostic for the occlusion test in WindowVisibilityWatcher.
#
# Replicates the same Z-order walk from outside the app, so a failure to detect
# "covered" can be pinned on the geometry test rather than on the collapse that
# follows it.

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Occ {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
    public static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
}
"@

$GW_HWNDPREV = 3
$GWL_EXSTYLE = -20
$WS_EX_TRANSPARENT = 0x20
$DWMWA_CLOAKED = 14

$app = Get-Process -Name 'HomeAssistant.Desktop' -ErrorAction SilentlyContinue
if (-not $app) { throw 'app not running' }
$h = $app.MainWindowHandle

$self = New-Object Occ+RECT
[Occ]::GetWindowRect($h, [ref]$self) | Out-Null
Write-Output ("app window: {0},{1} {2}x{3}  visible={4} iconic={5}" -f `
    $self.L, $self.T, ($self.R - $self.L), ($self.B - $self.T),
    [Occ]::IsWindowVisible($h), [Occ]::IsIconic($h))

$cl = 0
[Occ]::DwmGetWindowAttribute($h, $DWMWA_CLOAKED, [ref]$cl, 4) | Out-Null
Write-Output "app cloaked: $cl"
Write-Output ''
Write-Output 'windows above it in Z-order:'

$above = [Occ]::GetWindow($h, $GW_HWNDPREV)
$n = 0
$visibleAbove = 0
$covered = $false
while ($above -ne [IntPtr]::Zero -and $n -lt 5000) {
    $n++
    $r = New-Object Occ+RECT
    [Occ]::GetWindowRect($above, [ref]$r) | Out-Null
    $vis = [Occ]::IsWindowVisible($above)
    $ico = [Occ]::IsIconic($above)
    $ex = [int64][Occ]::GetWindowLongPtrW($above, $GWL_EXSTYLE)
    $transparent = ($ex -band $WS_EX_TRANSPARENT) -ne 0
    $c = 0
    [Occ]::DwmGetWindowAttribute($above, $DWMWA_CLOAKED, [ref]$c, 4) | Out-Null
    $contains = ($r.L -le $self.L) -and ($r.T -le $self.T) -and ($r.R -ge $self.R) -and ($r.B -ge $self.B)

    if ($vis -and -not $ico -and $c -eq 0 -and ($r.R - $r.L) -gt 0) {
        $visibleAbove++
        $title = [Occ]::Text($above)
        if ($title.Length -gt 40) { $title = $title.Substring(0, 40) }
        Write-Output ("  {0,-42} {1,5},{2,5} {3,5}x{4,-5} transparent={5,-5} CONTAINS={6}" -f `
            $title, $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T), $transparent, $contains)
        if ($contains -and -not $transparent) { $covered = $true }
    }
    $above = [Occ]::GetWindow($above, $GW_HWNDPREV)
}

Write-Output ''
Write-Output "walked $n windows above ($visibleAbove of them visible); VERDICT covered = $covered"
