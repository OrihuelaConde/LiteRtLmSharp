#!/usr/bin/env pwsh
<#
Restores the native LiteRT-LM binaries into runtimes/<rid>/native/ from THIS repo's
`native-<version>` GitHub Release (built by .github/workflows/build-native.yml).

Run once after cloning, before building the samples/tests. The repo is private, so this
uses the `gh` CLI (must be installed and authenticated: `gh auth login`).

Usage:
  pwsh scripts/restore-natives.ps1                 # current desktop OS only
  pwsh scripts/restore-natives.ps1 -Rid android-arm64
  pwsh scripts/restore-natives.ps1 -All
#>
param(
    [string]$Version = 'v0.13.1',
    [ValidateSet('win-x64', 'linux-x64', 'android-arm64', 'osx-arm64')]
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
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh -and (Test-Path 'C:\Program Files\GitHub CLI\gh.exe')) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
else { $gh = $gh.Source }
if (-not $gh) { throw "GitHub CLI (gh) not found. Install it and run 'gh auth login'." }

if ($All) { $Rid = @($assets.Keys) }
elseif (-not $Rid) {
    $Rid = @(if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' }
             else { throw 'Unsupported OS; pass -Rid explicitly.' })
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$tag = "native-$Version"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "litertlmsharp-natives"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null

# checksums.txt accompanies the release; verify what we download against it.
& $gh release download $tag --repo $repo --pattern 'checksums.txt' --dir $tmp --clobber
$checksums = @{}
Get-Content (Join-Path $tmp 'checksums.txt') | ForEach-Object {
    $parts = $_ -split '\s+', 2
    if ($parts.Count -eq 2) { $checksums[$parts[1].Trim().TrimStart('*')] = $parts[0].ToLower() }
}

foreach ($r in $Rid) {
    $asset = $assets[$r]
    Write-Host "Restoring $r ($asset) ..."
    & $gh release download $tag --repo $repo --pattern $asset --dir $tmp --clobber

    $file = Join-Path $tmp $asset
    $actual = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLower()
    if ($checksums.ContainsKey($asset) -and $checksums[$asset] -ne $actual) {
        throw "Checksum mismatch for ${asset}: expected $($checksums[$asset]), got $actual"
    }

    $dest = Join-Path $repoRoot "runtimes/$r/native"
    New-Item -ItemType Directory -Force $dest | Out-Null
    tar -xzf $file -C $dest
    Get-ChildItem $dest -Filter '._*' | Remove-Item -Force   # macOS AppleDouble metadata
    Write-Host "  -> $((Get-ChildItem $dest -File).Count) files in runtimes/$r/native"
}

Write-Host 'Done.'
