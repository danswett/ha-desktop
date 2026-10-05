# Measurement helpers shared by the comparison runs.
#
# Everything here reads from outside the processes under test - window titles and
# OS process counters - so the Chrome app and the WinUI app are measured by exactly
# the same method, with no debugger or instrumentation attached to either.

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class Win {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public const uint WM_CLOSE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;

    public static List<string> Titles() {
        var found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            // Deliberately not filtered on IsWindowVisible: the WinUI app parks
            // itself in the tray, where the window is hidden but its title - and
            // therefore its counters - are still readable.
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (t.Length > 0) {
                uint pid; GetWindowThreadProcessId(h, out pid);
                found.Add(h.ToInt64() + "|" + pid + "|" + (IsWindowVisible(h) ? "1" : "0") + "|" + t);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

function Get-WindowsMatching {
    param([Parameter(Mandatory)][string]$Pattern)
    [Win]::Titles() | ForEach-Object {
        $parts = $_ -split '\|', 4
        if ($parts[3] -match $Pattern) {
            [pscustomobject]@{
                Hwnd    = [IntPtr][int64]$parts[0]
                Pid     = [int]$parts[1]
                Visible = ($parts[2] -eq '1')
                Title   = $parts[3]
            }
        }
    }
}

function ConvertFrom-BenchTitle {
    param([Parameter(Mandatory)][string]$Title)
    # BENCH <id> f=<frames> j50=<n> j250=<n> t=<ticks> dl=<lateTicks> e=<elapsed>
    if ($Title -notmatch '^BENCH (\S+) f=(\d+) j50=(\d+) j250=(\d+) t=(\d+) dl=(\d+) e=([\d.]+)') { return $null }
    [pscustomobject]@{
        Id        = $Matches[1]
        Frames    = [int]$Matches[2]
        Janky50   = [int]$Matches[3]
        Janky250  = [int]$Matches[4]
        Ticks     = [int]$Matches[5]
        LateTicks = [int]$Matches[6]
        Elapsed   = [double]$Matches[7]
    }
}

function Get-BenchReading {
    param([Parameter(Mandatory)][string]$Id)
    $w = Get-WindowsMatching -Pattern "^BENCH $Id " | Select-Object -First 1
    if (-not $w) { return $null }
    ConvertFrom-BenchTitle -Title $w.Title
}

# ---- process trees ---------------------------------------------------------

function Get-ProcessTreePids {
    <#
      Walks children by ParentProcessId. Chrome and WebView2 both fan out into
      helper processes, and the cost of either host is the whole tree, not the
      process that happens to own the window.

      Win32_Process reports ProcessId and ParentProcessId as UInt32. A UInt32 key
      does not match an Int32 lookup in a .NET hashtable, so everything is cast to
      int on the way in - without that every lookup misses and the walk returns
      only the root.
    #>
    param([Parameter(Mandatory)][int]$RootPid)

    $byParent = @{}
    foreach ($p in (Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)) {
        $parent = [int]$p.ParentProcessId
        if (-not $byParent.ContainsKey($parent)) { $byParent[$parent] = [System.Collections.Generic.List[int]]::new() }
        $byParent[$parent].Add([int]$p.ProcessId)
    }

    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($RootPid)
    while ($queue.Count -gt 0) {
        $cur = $queue.Dequeue()
        if (-not $seen.Add($cur)) { continue }
        if ($byParent.ContainsKey($cur)) {
            foreach ($child in $byParent[$cur]) { $queue.Enqueue($child) }
        }
    }

    , ([int[]]$seen)
}

function Get-TreeSnapshot {
    param([Parameter(Mandatory)][int[]]$Pids)

    $cpu = [TimeSpan]::Zero
    $ws = 0L
    $priv = 0L
    $threads = 0
    $count = 0

    foreach ($id in $Pids) {
        try {
            $p = Get-Process -Id $id -ErrorAction Stop
            $cpu += $p.TotalProcessorTime
            $ws += $p.WorkingSet64
            $priv += $p.PrivateMemorySize64
            $threads += $p.Threads.Count
            $count++
        }
        catch { }
    }

    [pscustomobject]@{
        At        = [DateTime]::UtcNow
        Processes = $count
        Threads   = $threads
        CpuTime   = $cpu
        WorkingSet= $ws
        Private   = $priv
    }
}

function Measure-TreeCpu {
    <#
      CPU as a percentage of one core, from TotalProcessorTime deltas over a wall
      clock interval. Sampling the counter directly avoids the smoothing and the
      first-sample-is-garbage behaviour of Get-Counter.
    #>
    param(
        [Parameter(Mandatory)][int]$RootPid,
        [int]$Seconds = 30
    )

    $pids = Get-ProcessTreePids -RootPid $RootPid
    $a = Get-TreeSnapshot -Pids $pids
    Start-Sleep -Seconds $Seconds
    $pids = Get-ProcessTreePids -RootPid $RootPid
    $b = Get-TreeSnapshot -Pids $pids

    $wall = ($b.At - $a.At).TotalSeconds
    $cpuSec = ($b.CpuTime - $a.CpuTime).TotalSeconds

    [pscustomobject]@{
        Processes    = $b.Processes
        Threads      = $b.Threads
        CpuPercent   = [math]::Round(($cpuSec / $wall) * 100, 2)
        CpuSeconds   = [math]::Round($cpuSec, 2)
        WallSeconds  = [math]::Round($wall, 1)
        WorkingSetMB = [math]::Round($b.WorkingSet / 1MB, 1)
        PrivateMB    = [math]::Round($b.Private / 1MB, 1)
    }
}
