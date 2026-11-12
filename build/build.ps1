<#
  Compilación reproducible de Fran Capture Studio en Windows:
    1. descarga y verifica FFmpeg, 2. compila, 3. ejecuta las pruebas,
    4. publica la versión autocontenida win-x64, 5. genera FranCaptureStudio-Setup.exe con Inno Setup.
  Requisitos: SDK de .NET 10 e Inno Setup 6 (winget install JRSoftware.InnoSetup).
  Uso:  pwsh build\build.ps1 [-SkipTests] [-SkipInstaller]
#>
param([switch]$SkipTests, [switch]$SkipInstaller, [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
[xml]$props = Get-Content "Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
Write-Host "== Fran Capture Studio $version ==" -ForegroundColor Cyan

& "$PSScriptRoot\fetch-ffmpeg.ps1"

dotnet build FranCaptureStudio.sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación" }

if (-not $SkipTests) {
  New-Item -ItemType Directory -Force artifacts\test-results | Out-Null
  $env:FCS_FFMPEG = Join-Path $root "third_party\ffmpeg\ffmpeg.exe"
  dotnet test tests\FranCapture.Core.Tests -c $Configuration --nologo --logger "trx;LogFileName=core.trx" --logger "console;verbosity=minimal" --results-directory artifacts\test-results
  if ($LASTEXITCODE -ne 0) { throw "Fallaron las pruebas unitarias" }
  dotnet test tests\FranCaptureStudio.WinTests -c $Configuration --nologo --logger "trx;LogFileName=windows.trx" --logger "console;verbosity=normal" --results-directory artifacts\test-results
  if ($LASTEXITCODE -ne 0) { throw "Fallaron las pruebas de integración de Windows" }
}

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\FranCaptureStudio\FranCaptureStudio.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación" }
Copy-Item docs\MANUAL.md, README.md -Destination publish -ErrorAction SilentlyContinue
if (-not (Test-Path publish\ffmpeg\ffmpeg.exe)) { throw "La publicación no contiene ffmpeg\ffmpeg.exe" }

if (-not $SkipInstaller) {
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) { $cmd = Get-Command iscc -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
  if (-not $iscc) { throw "No se encontró Inno Setup 6 (ISCC.exe). Instálalo con: winget install JRSoftware.InnoSetup" }
  New-Item -ItemType Directory -Force artifacts | Out-Null
  & $iscc "/DAppVersion=$version" "/DSourceDir=$root\publish" installer\FranCaptureStudio.iss
  if ($LASTEXITCODE -ne 0) { throw "Falló la generación del instalador" }
  $setup = Get-Item artifacts\FranCaptureStudio-Setup.exe
  $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
  "$hash  FranCaptureStudio-Setup.exe" | Set-Content artifacts\FranCaptureStudio-Setup.exe.sha256 -Encoding ASCII
  Write-Host ("Instalador: {0} ({1:N1} MB)  SHA-256 {2}" -f $setup.FullName, ($setup.Length / 1MB), $hash) -ForegroundColor Green
}
