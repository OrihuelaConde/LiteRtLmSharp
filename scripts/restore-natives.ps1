#!/usr/bin/env pwsh
<#
Restores the native LiteRT-LM binaries into runtimes/<rid>/native/ from this repo's
`native-<version>` GitHub Release (built by .github/workflows/build-native.yml).

Run once after cloning, before building the samples/tests. Downloads over plain HTTPS —
no GitHub CLI or authentication needed.

Usage:
  pwsh scripts/restore-natives.ps1                 # current desktop OS only
  pwsh scripts/restore-natives.ps1 -Rid android-arm64
  pwsh scripts/restore-natives.ps1 -All
#>
param(
    [string]$Version = 'v0.15.0',
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
    # Start from a clean directory: a version switch would otherwise leave stale binaries from the
    # previous native set behind (found the hard way on the v0.14.0 -> v0.15.0 repin, where the
    # since-dropped prefixless twin DLLs survived the restore).
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dest | Out-Null
    tar -xzf $file -C $dest
    Get-ChildItem $dest -Filter '._*' | Remove-Item -Force   # macOS AppleDouble metadata
    if ($r -eq 'ios-arm64') {
        Get-ChildItem $dest -Filter '*.dylib' -File | Remove-Item -Force   # the tar also carries raw dylibs; keep only xcframeworks/
        Write-Host "  -> restored runtimes/$r/xcframeworks"
    }
    else {
        Write-Host "  -> $((Get-ChildItem $dest -File).Count) files in runtimes/$r/native"
    }
}

Write-Host 'Done.'
