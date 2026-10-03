#Requires -RunAsAdministrator
# Instala (o actualiza) SerialBridge como servicio de Windows con inicio automatico
# y reinicio automatico si el proceso falla.
param(
    # Version de 64 o 32 bits segun el Windows del equipo
    [string]$Source = "$PSScriptRoot\output\publish\$(if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' })",
    [string]$InstallDir = "$env:ProgramFiles\SerialBridge",
    [string]$ServiceName = 'SerialBridge'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path "$Source\SerialBridge.exe")) {
    throw "No se encontro $Source\SerialBridge.exe. Ejecute primero .\build.ps1 -SkipInstaller"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Deteniendo $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path "$Source\*" -Destination $InstallDir -Recurse -Force

if (-not $existing) {
    New-Service -Name $ServiceName `
        -BinaryPathName "`"$InstallDir\SerialBridge.exe`"" `
        -DisplayName 'Lector USB' `
        -Description 'Lee la lectora conectada como puerto COM y reenvia cada trama por WebSocket en 127.0.0.1.' `
        -StartupType Automatic | Out-Null
}

# Reiniciar a los 5 s si el proceso termina inesperadamente.
sc.exe failure $ServiceName reset= 86400 actions= restart/2000/restart/2000/restart/5000 | Out-Null

Start-Service -Name $ServiceName

$port = 21818
$settingsFile = "$env:ProgramData\SerialBridge\settings.json"
if (Test-Path $settingsFile) {
    $port = (Get-Content $settingsFile -Raw | ConvertFrom-Json).webSocketPort
}
Write-Host ""
Write-Host "Servicio $ServiceName instalado y en ejecucion."
Write-Host "  Configuracion : http://127.0.0.1:$port/"
Write-Host "  WebSocket     : ws://127.0.0.1:$port/"
Write-Host "  Ajustes       : $settingsFile"
