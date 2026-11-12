# Resumen breve del resultado de CI (errores de compilación, pruebas fallidas, prueba de humo).
$ErrorActionPreference = "Continue"
function Short([string]$s, [int]$n = 420) { $s = ($s -replace '\s+', ' ').Trim(); if ($s.Length -gt $n) { $s.Substring(0, $n) + '…' } else { $s } }
Write-Host "=== DIAGNOSTICO ==="
if (Test-Path artifacts\build.log) {
  $errs = Select-String -Path artifacts\build.log -Pattern ' error [A-Z]+\d+|Exception:|No se encontr|Fall[oó]' | Select-Object -First 12
  foreach ($e in $errs) { Write-Host ("BUILD: " + (Short $e.Line 300)) }
}
foreach ($trx in Get-ChildItem artifacts\test-results -Filter *.trx -ErrorAction SilentlyContinue) {
  [xml]$x = Get-Content $trx.FullName
  $c = $x.TestRun.ResultSummary.Counters
  Write-Host ("TRX {0}: total={1} ok={2} fallidas={3}" -f $trx.Name, $c.total, $c.passed, $c.failed)
  foreach ($r in $x.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed' | Select-Object -First 14) {
    $name = ($r.testName -split '\.')[-1]
    Write-Host ("  FALLA {0}: {1}" -f $name, (Short ($r.Output.ErrorInfo.Message + ' @ ' + (($r.Output.ErrorInfo.StackTrace -split "`n" | Where-Object { $_ -match 'WinTests\.cs|src\\' } | Select-Object -First 2) -join ' | '))))
  }
  foreach ($r in $x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Passed' -and $_.Output.StdOut } | Select-Object -First 12) {
    Write-Host ("  INFO {0}: {1}" -f (($r.testName -split '\.')[-1]), (Short $r.Output.StdOut 260))
  }
}
Get-ChildItem $env:TEMP -Filter "fcs-tests-*" -Directory -ErrorAction SilentlyContinue | ForEach-Object { if (Test-Path "$($_.FullName)\omitidas.txt") { Get-Content "$($_.FullName)\omitidas.txt" | ForEach-Object { Write-Host $_ } } }
if (Test-Path artifacts\smoke-results.txt) { Get-Content artifacts\smoke-results.txt | ForEach-Object { Write-Host ("SMOKE " + $_) } }
if (Test-Path artifacts\ui) { Write-Host ("UI: " + ((Get-ChildItem artifacts\ui | ForEach-Object { $_.Name }) -join ', ')) }
if (Test-Path artifacts\FranCaptureStudio-Setup.exe) { Write-Host ("SETUP: {0:N1} MB" -f ((Get-Item artifacts\FranCaptureStudio-Setup.exe).Length / 1MB)) }
Write-Host "=== FIN ==="
