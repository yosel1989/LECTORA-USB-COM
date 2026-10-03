#Requires -RunAsAdministrator
# Desinstala el servicio. Con -RemoveFiles borra tambien el programa y la configuracion.
param(
    [string]$InstallDir = "$env:ProgramFiles\SerialBridge",
    [string]$ServiceName = 'SerialBridge',
    [switch]$RemoveFiles
)
$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:30')
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Servicio $ServiceName eliminado."
} else {
    Write-Host "El servicio $ServiceName no esta instalado."
}

if ($RemoveFiles) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $InstallDir, "$env:ProgramData\SerialBridge"
    Write-Host "Archivos eliminados."
}
