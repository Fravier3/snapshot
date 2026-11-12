<#
  Prueba de humo de extremo a extremo sobre el instalador real (se ejecuta en CI con un runner de Windows):
  instala en silencio, comprueba archivos y accesos directos, inicia la aplicación, verifica instancia única,
  pulsa F9 de verdad y comprueba que aparece el selector, captura pantalla, cierra, y desinstala.
  Escribe artifacts\smoke-results.txt con el resultado de cada comprobación.
#>
param([string]$Setup = "artifacts\FranCaptureStudio-Setup.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$results = New-Object System.Collections.Generic.List[string]
$failed = $false
function Check([string]$name, [bool]$ok, [string]$detail = "") {
  $line = ("{0}  {1}{2}" -f ($(if ($ok) { "OK   " } else { "FALLO" })), $name, $(if ($detail) { "  [$detail]" } else { "" }))
  $script:results.Add($line); Write-Host $line
  if (-not $ok) { $script:failed = $true }
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W32 {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
"@
function Shot([string]$name) {
  try {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.X, $b.Y, 0, 0, $b.Size); $g.Dispose()
    $bmp.Save((Join-Path $root "artifacts\ui\$name.png")); $bmp.Dispose()
  } catch { Write-Host "captura $name no disponible: $_" }
}
function Procs { @(Get-Process FranCaptureStudio -ErrorAction SilentlyContinue) }
function WaitUntil([scriptblock]$cond, [int]$seconds = 20) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $seconds) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 300 }
  return $false
}

New-Item -ItemType Directory -Force artifacts\ui | Out-Null
$data = Join-Path $env:TEMP ("fcs-smoke-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force "$data\Roaming" | Out-Null
$env:FCS_DATA_DIR = $data
# Configuración inicial ya completada, con F9 para el selector y sin abrir el editor tras capturar.
@'
{ "SchemaVersion": 1, "FirstRunCompleted": true, "Sounds": false, "Notifications": false, "Language": "es",
  "Hotkeys": { "CaptureRegion": "F9" },
  "Automations": [ { "Trigger": "Capture", "Action": "AutoSave", "Enabled": true }, { "Trigger": "Capture", "Action": "CopyToClipboard", "Enabled": true } ] }
'@ | Set-Content "$data\Roaming\settings.json" -Encoding UTF8
$personal = Join-Path $data "Capturas\documento-personal.txt"
New-Item -ItemType Directory -Force (Split-Path $personal) | Out-Null
"no borrar" | Set-Content $personal

# 1. Instalación silenciosa
$setupPath = (Resolve-Path $Setup).Path
$sig = Get-AuthenticodeSignature $setupPath
$results.Add("INFO   Firma digital del instalador: $($sig.Status) (no se afirma que esté firmado)")
$p = Start-Process $setupPath -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS="desktopicon"', "/LOG=`"$root\artifacts\install.log`"" -Wait -PassThru
Check "El instalador se ejecuta y termina con código 0" ($p.ExitCode -eq 0) "código $($p.ExitCode)"
$app = Join-Path $env:LOCALAPPDATA "Programs\Fran Capture Studio"
$exe = Join-Path $app "FranCaptureStudio.exe"
Check "Instala FranCaptureStudio.exe" (Test-Path $exe) $exe
Check "Instala FFmpeg (ffmpeg\ffmpeg.exe)" (Test-Path (Join-Path $app "ffmpeg\ffmpeg.exe"))
Check "Instala el runtime autocontenido (coreclr.dll)" (Test-Path (Join-Path $app "coreclr.dll"))
Check "Instala los avisos de licencias" (Test-Path (Join-Path $app "THIRD-PARTY-NOTICES.md"))
Check "Instala el desinstalador" (Test-Path (Join-Path $app "unins000.exe"))
if (Test-Path $exe) {
  $vi = (Get-Item $exe).VersionInfo
  Check "El ejecutable declara producto, fabricante y versión" (($vi.ProductName -eq "Fran Capture Studio") -and ($vi.CompanyName -like "Desarrollos Francisco*") -and ($vi.FileVersion -like "1.*")) "$($vi.ProductName) | $($vi.CompanyName) | $($vi.FileVersion)"
}
$startLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Fran Capture Studio.lnk"
$deskLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Fran Capture Studio.lnk"
Check "Acceso directo en el menú Inicio" (Test-Path $startLnk)
Check "Acceso directo en el escritorio (tarea opcional marcada en esta prueba)" (Test-Path $deskLnk)
$run = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
Check "No activa el inicio con Windows si no se pide" (-not ($run.PSObject.Properties.Name -contains "FranCaptureStudio"))
$unreg = Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue | Get-ItemProperty | Where-Object { $_.DisplayName -eq "Fran Capture Studio" }
Check "Registra la desinstalación con número de versión" ($unreg -and $unreg.DisplayVersion -like "1.*") "$($unreg.DisplayVersion)"

if (Test-Path $startLnk) {
  # 2. Inicio desde el acceso directo
  $target = (New-Object -ComObject WScript.Shell).CreateShortcut($startLnk).TargetPath
  Check "El acceso directo apunta al ejecutable instalado" ($target -eq $exe) $target
  Start-Process $startLnk
  $started = WaitUntil { (Procs).Count -ge 1 -and (Procs | Where-Object { $_.MainWindowTitle -eq "Fran Capture Studio" }) } 40
  Check "La aplicación inicia desde el acceso directo y muestra la ventana principal" $started
  Start-Sleep 2; Shot "smoke-1-principal"

  # 3. Instancia única
  Start-Process $exe; Start-Sleep 4
  Check "Una segunda ejecución no crea procesos duplicados" ((Procs).Count -eq 1) "procesos: $((Procs).Count)"

  # 4. Ocultar en la bandeja (cerrar ventana) y restaurar con una segunda ejecución
  $main = Procs | Select-Object -First 1
  $null = $main.CloseMainWindow(); Start-Sleep 2
  Check "Cerrar la ventana deja el proceso activo en la bandeja" ((Procs).Count -eq 1)
  $hidden = -not (Procs | Where-Object { $_.MainWindowTitle -eq "Fran Capture Studio" })
  Check "La ventana principal queda oculta" $hidden

  # 5. F9 real con la ventana oculta: debe aparecer el selector de captura
  [W32]::Key(0x78)
  $selector = WaitUntil { $h = [W32]::FindWindow($null, "Fran Capture Studio – selector"); ($h -ne [IntPtr]::Zero) -and [W32]::IsWindowVisible($h) } 10
  Check "La tecla F9 abre el selector de captura con la aplicación en la bandeja" $selector
  Start-Sleep 1; Shot "smoke-2-selector-f9"
  [W32]::Key(0x1B); Start-Sleep 1
  $gone = WaitUntil { [W32]::FindWindow($null, "Fran Capture Studio – selector") -eq [IntPtr]::Zero } 6
  Check "Escape cancela el selector" $gone

  # 6. Captura de pantalla completa por orden externa: se guarda en la carpeta configurada
  $before = @(Get-ChildItem "$data\Capturas" -Filter *.png -ErrorAction SilentlyContinue).Count
  Start-Process $exe -ArgumentList "--capture-screen"
  $savedOk = WaitUntil { @(Get-ChildItem "$data\Capturas" -Filter *.png -ErrorAction SilentlyContinue).Count -gt $before } 20
  $file = Get-ChildItem "$data\Capturas" -Filter *.png -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  Check "Una captura se guarda como PNG en la carpeta de capturas" $savedOk "$($file.Name) $($file.Length) bytes"
  if ($file) {
    $img = [System.Drawing.Image]::FromFile($file.FullName); $dim = "$($img.Width)x$($img.Height)"; $okDim = ($img.Width -eq [System.Windows.Forms.SystemInformation]::VirtualScreen.Width); $img.Dispose()
    Check "La captura tiene el tamaño del escritorio" $okDim $dim
  }
  Check "La base de datos de la biblioteca existe (SQLite)" (Test-Path "$data\Roaming\library.db")

  # 7. Restaurar la ventana
  Start-Process $exe
  $restored = WaitUntil { Procs | Where-Object { $_.MainWindowTitle -eq "Fran Capture Studio" } } 15
  Check "La ventana se restaura desde el modo bandeja" $restored
  Shot "smoke-3-restaurada"

  # 8. Salida completa
  Start-Process $exe -ArgumentList "--exit"
  $exited = WaitUntil { (Procs).Count -eq 0 } 20
  Check "«Salir completamente» termina el proceso" $exited
  if (-not $exited) { Procs | Stop-Process -Force }
  [W32]::Key(0x78); Start-Sleep 2
  Check "Tras salir, F9 ya no abre nada (atajos liberados)" ([W32]::FindWindow($null, "Fran Capture Studio – selector") -eq [IntPtr]::Zero)

  # 9. La configuración persiste tras reiniciar
  $cfg = Get-Content "$data\Roaming\settings.json" -Raw | ConvertFrom-Json
  Check "La configuración persiste (F9 y región guardadas en settings.json)" ($cfg.Hotkeys.CaptureRegion -eq "F9")
}

# 10. Desinstalación
$unins = Join-Path $app "unins000.exe"
if (Test-Path $unins) {
  $p = Start-Process $unins -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
  Start-Sleep 5
  Check "El desinstalador termina con código 0" ($p.ExitCode -eq 0)
  Check "Elimina los archivos del programa" (-not (Test-Path $exe))
  Check "Elimina los accesos directos" ((-not (Test-Path $startLnk)) -and (-not (Test-Path $deskLnk)))
  Check "No elimina capturas ni documentos personales" ((Test-Path $personal) -and (@(Get-ChildItem "$data\Capturas" -Filter *.png).Count -ge 1))
  Check "Conserva la configuración para futuras actualizaciones" (Test-Path "$data\Roaming\settings.json")
}
if (Test-Path "$data\Local\logs\app.log") { Copy-Item "$data\Local\logs\app.log" artifacts\smoke-app.log -Force }
$results | Set-Content artifacts\smoke-results.txt -Encoding UTF8
if ($failed) { Write-Error "Hay comprobaciones fallidas en la prueba de humo."; exit 1 }
Write-Host "Prueba de humo completada sin fallos." -ForegroundColor Green
