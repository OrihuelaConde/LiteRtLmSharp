#!/usr/bin/env pwsh
# Restores the native LiteRT-LM binaries into runtimes/<rid>/native/.
# These are not committed (183MB of third-party DLLs); run this once after cloning,
# before `dotnet build`. Fase 2 will replace this with CI-built, version-matched binaries.
#
# Source: prebuilt LiteRT-LM binaries published by flutter_gemma as GitHub Release assets.
# See docs/native-abi.md for details and the known 0.12.0-a ABI quirks.

$ErrorActionPreference = 'Stop'

$version = '0.12.0-a'
$base = "https://github.com/DenisovAV/flutter_gemma/releases/download/native-v$version"

# rid -> @{ asset; sha256; pattern }
$targets = @(
    @{ rid = 'win-x64';   asset = 'litertlm-windows_x86_64.tar.gz'; sha256 = 'b7264091c05001ef84e53761dfee331f761e3a2362b36b28ab2ce39666400d76'; pattern = '*.dll' }
    @{ rid = 'linux-x64'; asset = 'litertlm-linux_x86_64.tar.gz';   sha256 = '930296b010ecc316c6b6fc4ed1c722b275b4064b59b5aad8ff7b858e9149c0d7'; pattern = '*.so'  }
)

$repoRoot = Split-Path -Parent $PSScriptRoot

# Only restore the current OS's RID by default (override with -All via env if needed).
$wantRid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { $null }
if (-not $wantRid) { throw "Unsupported OS. Only win-x64 and linux-x64 are available in the PoC." }

$t = $targets | Where-Object { $_.rid -eq $wantRid } | Select-Object -First 1
$dest = Join-Path $repoRoot "runtimes/$($t.rid)/native"
$tmp  = Join-Path ([System.IO.Path]::GetTempPath()) $t.asset

Write-Host "Downloading $($t.asset) ..."
Invoke-WebRequest -Uri "$base/$($t.asset)" -OutFile $tmp

$actual = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLower()
if ($actual -ne $t.sha256) { throw "Checksum mismatch for $($t.asset): expected $($t.sha256), got $actual" }
Write-Host "Checksum OK."

New-Item -ItemType Directory -Force $dest | Out-Null
$extract = Join-Path ([System.IO.Path]::GetTempPath()) "litertlm-$($t.rid)"
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $extract | Out-Null

tar -xzf $tmp -C $extract
Get-ChildItem $extract -Recurse -Filter $t.pattern | Where-Object { $_.Name -notlike '._*' } |
    Copy-Item -Destination $dest -Force

$count = (Get-ChildItem $dest -Filter $t.pattern).Count
Write-Host "Restored $count native files to runtimes/$($t.rid)/native/"

Remove-Item $tmp, $extract -Recurse -Force -ErrorAction SilentlyContinue
