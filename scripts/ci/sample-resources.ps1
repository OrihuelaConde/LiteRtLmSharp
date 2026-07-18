# Samples runner resource state to a CSV until killed. Started as a background process by
# model-tests.yml (crash forensics: an intermittent native crash in the overflow-guard test on
# 2026-07-17 left no way to tell whether the shared runner was under memory pressure).
# Cross-platform: pwsh on the ubuntu/windows/macos runner images.
param(
    [Parameter(Mandatory)][string]$OutFile,
    [int]$IntervalSeconds = 5
)

$dir = Split-Path $OutFile
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
'timestampUtc,availMemMB,totalMemMB,dotnetWorkingSetMB,diskFreeGB' | Set-Content $OutFile

while ($true) {
    if ($IsWindows) {
        $os = Get-CimInstance Win32_OperatingSystem
        $avail = [math]::Round($os.FreePhysicalMemory / 1KB)      # counters are in KB
        $total = [math]::Round($os.TotalVisibleMemorySize / 1KB)
    }
    elseif ($IsLinux) {
        $mi = @{}
        foreach ($l in Get-Content /proc/meminfo) {
            if ($l -match '^(\w+):\s+(\d+)') { $mi[$Matches[1]] = [long]$Matches[2] }
        }
        $avail = [math]::Round($mi['MemAvailable'] / 1KB)
        $total = [math]::Round($mi['MemTotal'] / 1KB)
    }
    else {
        $total = [math]::Round([long](sysctl -n hw.memsize) / 1MB)
        $pageSize = [long](sysctl -n hw.pagesize)
        $freePages = 0
        foreach ($l in vm_stat) {
            if ($l -match 'Pages (?:free|inactive|purgeable):\s+(\d+)') { $freePages += [long]$Matches[1] }
        }
        $avail = [math]::Round($freePages * $pageSize / 1MB)
    }

    # Working set of the test processes (dotnet + testhost) — the model plus KV cache live here.
    $ws = (Get-Process | Where-Object { $_.ProcessName -match '^(dotnet|testhost)' } |
        Measure-Object WorkingSet64 -Sum).Sum
    $wsMB = [math]::Round(($ws ?? 0) / 1MB)

    $diskGB = try {
        $root = [System.IO.Path]::GetPathRoot((Get-Location).Path)
        [math]::Round(([System.IO.DriveInfo]::new($root)).AvailableFreeSpace / 1GB, 1)
    } catch { -1 }

    Add-Content $OutFile "$([DateTime]::UtcNow.ToString('o')),$avail,$total,$wsMB,$diskGB"
    Start-Sleep -Seconds $IntervalSeconds
}
