#!/usr/bin/env pwsh
<#
Restores the native LiteRT-LM binaries into runtimes/<rid>/native/ from this repo's
`native-<version>` GitHub Release (published by .github/workflows/native-release.yml from Google's
official LiteRT-LM C API prebuilts).

Run once after cloning, before building the samples/tests. Downloads over plain HTTPS —
no GitHub CLI or authentication needed.

Usage:
  pwsh scripts/restore-natives.ps1                 # current desktop OS only
  pwsh scripts/restore-natives.ps1 -Rid android-arm64
  pwsh scripts/restore-natives.ps1 -All
#>
param(
    [string]$Version = 'v0.16.0',
    [ValidateSet('win-x64', 'linux-x64', 'android-arm64', 'osx-arm64', 'ios-arm64')]
    [string[]]$Rid,
    [switch]$All
)

$ErrorActionPreference = 'Stop'

$repo = 'OrihuelaConde/LiteRtLmSharp'
$assets = @{
    'win-x64'       = 'litertlm-windows_x86_64.tar.gz'
    'linux-x64'     = 'litertlm-linux_x86_64.tar.gz'
    'android-arm64' = 'litertlm-android_arm64.tar.gz'
    'osx-arm64'     = 'litertlm-macos_arm64.tar.gz'
    'ios-arm64'     = 'litertlm-ios_arm64.tar.gz'
}

if ($All) { $Rid = @($assets.Keys) }
elseif (-not $Rid) {
    $Rid = @(if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' }
             else { throw 'Unsupported OS; pass -Rid explicitly.' })
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$tag = "native-$Version"
$base = "https://github.com/$repo/releases/download/$tag"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "litertlmsharp-natives"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null

# checksums.txt accompanies the release; verify what we download against it.
Invoke-WebRequest "$base/checksums.txt" -OutFile (Join-Path $tmp 'checksums.txt')
$checksums = @{}
Get-Content (Join-Path $tmp 'checksums.txt') | ForEach-Object {
    $parts = $_ -split '\s+', 2
    if ($parts.Count -eq 2) { $checksums[$parts[1].Trim().TrimStart('*')] = $parts[0].ToLower() }
}

foreach ($r in $Rid) {
    $asset = $assets[$r]
    Write-Host "Restoring $r ($asset) ..."
    $file = Join-Path $tmp $asset
    Invoke-WebRequest "$base/$asset" -OutFile $file

    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLower()
    if ($checksums.ContainsKey($asset) -and $checksums[$asset] -ne $actual) {
        throw "Checksum mismatch for ${asset}: expected $($checksums[$asset]), got $actual"
    }

    # iOS ships .xcframeworks (not a runtimes/<rid>/native dlopen layout); extract at the rid root.
    $dest = if ($r -eq 'ios-arm64') { Join-Path $repoRoot "runtimes/$r" } else { Join-Path $repoRoot "runtimes/$r/native" }

    # Extract to a STAGING dir first, then swap into place. Two failure modes this avoids (both
    # found in review): a failed tar after an in-place delete would leave an EMPTY native dir with
    # exit 0, and a delete that silently fails on a file locked by testhost/VS would leave stale
    # binaries from the previous native set mixed with the new (the v0.14.0 -> v0.15.0 repin hit
    # exactly that with the since-dropped prefixless twin DLLs). -LiteralPath everywhere so a repo
    # path containing PowerShell wildcard characters cannot turn the delete into a silent no-op.
    $staging = Join-Path $tmp "extract-$r"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force $staging | Out-Null
    tar -xzf $file -C $staging
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $asset (exit $LASTEXITCODE)" }
    Get-ChildItem $staging -Filter '._*' | Remove-Item -Force   # macOS AppleDouble metadata

    # Swap: clean the destination (NO SilentlyContinue — a locked file must fail loudly, not leave
    # a mixed tree; close the process holding runtimes/<rid> and re-run), then move the staging in.
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
    Move-Item -LiteralPath $staging -Destination $dest
    if ($r -eq 'ios-arm64') {
        Write-Host "  -> restored runtimes/$r/xcframeworks"
    }
    else {
        Write-Host "  -> $((Get-ChildItem $dest -File).Count) files in runtimes/$r/native"
    }
}

# Upstream's notice file for the official binaries: packed into every runtime package by the
# packaging projects when present (pack-nuget.yml does the same from the release).
$notice = 'THIRD_PARTY_NOTICES.litert-lm.txt'
$noticeDest = Join-Path $repoRoot "runtimes/$notice"
# Never leave a previous release's notice behind: the packaging projects pack whatever file exists.
if (Test-Path -LiteralPath $noticeDest) { Remove-Item -LiteralPath $noticeDest -Force }
try {
    New-Item -ItemType Directory -Force (Join-Path $repoRoot 'runtimes') | Out-Null
    $noticeTmp = Join-Path $tmp $notice
    Invoke-WebRequest "$base/$notice" -OutFile $noticeTmp
    $noticeHash = (Get-FileHash -Algorithm SHA256 $noticeTmp).Hash.ToLower()
    if ($checksums.ContainsKey($notice) -and $checksums[$notice] -ne $noticeHash) {
        throw "Checksum mismatch for ${notice}: expected $($checksums[$notice]), got $noticeHash"
    }
    Move-Item -LiteralPath $noticeTmp -Destination $noticeDest
    Write-Host "  -> restored runtimes/$notice"
}
catch {
    Write-Warning "Could not restore $notice from the release ($($_.Exception.Message)); local runtime packages will be packed WITHOUT it until it is restored."
}

Write-Host 'Done.'
