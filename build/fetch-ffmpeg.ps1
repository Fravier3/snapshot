<#
  Descarga FFmpeg (compilación LGPL de BtbN, sin componentes GPL) y verifica su SHA-256 contra la
  lista de sumas publicada junto a la propia versión. Deja ffmpeg.exe y su licencia en third_party\ffmpeg.
#>
param([string]$Asset = "ffmpeg-n8.1-latest-win64-lgpl-8.1.zip")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\ffmpeg"
if (Test-Path (Join-Path $dest "ffmpeg.exe")) { Write-Host "FFmpeg ya está presente en $dest"; exit 0 }
New-Item -ItemType Directory -Force $dest | Out-Null
$base = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp $Asset
Write-Host "Descargando $Asset…"
Invoke-WebRequest "$base/$Asset" -OutFile $zip
Invoke-WebRequest "$base/checksums.sha256" -OutFile (Join-Path $tmp "checksums.sha256")
$line = Get-Content (Join-Path $tmp "checksums.sha256") | Where-Object { $_ -match [regex]::Escape($Asset) + '$' } | Select-Object -First 1
if (-not $line) { throw "No se encontró la suma de verificación de $Asset" }
$expected = ($line -split '\s+')[0].ToLower()
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "SHA-256 no coincide para $Asset (esperado $expected, obtenido $actual)" }
Write-Host "SHA-256 verificado: $actual"
Expand-Archive $zip -DestinationPath $tmp -Force
$exe = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $dest "ffmpeg.exe") -Force
$lic = Get-ChildItem $tmp -Recurse -Filter "LICENSE*" | Select-Object -First 1
if ($lic) { Copy-Item $lic.FullName (Join-Path $dest "LICENSE.txt") -Force }
"$Asset`nsha256 $actual`nOrigen: $base/$Asset`nCódigo fuente: https://github.com/FFmpeg/FFmpeg (compilado por https://github.com/BtbN/FFmpeg-Builds)" | Set-Content (Join-Path $dest "VERSION.txt") -Encoding UTF8
Remove-Item $tmp -Recurse -Force
& (Join-Path $dest "ffmpeg.exe") -hide_banner -version | Select-Object -First 1
