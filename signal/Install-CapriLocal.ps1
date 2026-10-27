# Installs the Capri.exe you just built over your installed Capri, without running Capri-Setup.exe.
# (Use this when Windows Smart App Control blocks the new installer: it only copies the file the installer would copy.)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $PSScriptRoot
$built = Join-Path $dir 'Capri.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Capri'
$target = Join-Path $installDir 'Capri.exe'
if (-not (Test-Path $built)) { Write-Host "No hay Capri.exe compilado en $dir. Ejecuta antes Build-CapriWindows." -ForegroundColor Yellow; exit 1 }
if (-not (Test-Path (Join-Path $installDir '.capri-install'))) { Write-Host "No encuentro tu Capri instalada en $installDir. Usa Capri-Setup.exe la primera vez." -ForegroundColor Yellow; exit 1 }
$running = Get-Process Capri -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $target } catch { $false } }
if ($running) { Write-Host "Capri esta abierta. Cierrala desde la bandeja (Quit Capri) y vuelve a ejecutar esto." -ForegroundColor Yellow; exit 1 }
Copy-Item $target (Join-Path $installDir 'Capri.previous.exe') -Force -ErrorAction SilentlyContinue
Copy-Item $built $target -Force
Write-Host "Capri actualizada en $target (copia anterior: Capri.previous.exe). Abriendola..." -ForegroundColor Green
Start-Process -FilePath $target -WorkingDirectory $installDir
