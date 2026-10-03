# Compila SerialBridge para Windows de 64 y 32 bits y genera el instalador.
#
#   output\publish\x64\SerialBridge.exe          ejecutable para Windows de 64 bits
#   output\publish\x86\SerialBridge.exe          ejecutable para Windows de 32 bits
#   output\LectorUSB-Setup-<version>.exe        instalador unico: instala la version que corresponde al equipo
#
# Los ejecutables son autocontenidos: el equipo destino no necesita tener .NET instalado.
# Requiere Inno Setup 6:  winget install JRSoftware.InnoSetup
param(
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$output = Join-Path $root 'output'
$publishRoot = Join-Path $output 'publish'
$project = Join-Path $root 'SerialBridge.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }

foreach ($arch in 'x64', 'x86') {
    $dir = Join-Path $publishRoot $arch
    Write-Host "==> Publicando SerialBridge $version para win-$arch" -ForegroundColor Cyan
    dotnet publish $project `
        -c Release `
        -r "win-$arch" `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:IsTransformWebConfigDisabled=true `
        -o $dir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo para win-$arch" }
}

if ($SkipInstaller) {
    Write-Host ""
    Write-Host "Listo:" -ForegroundColor Green
    Write-Host "  64 bits : $publishRoot\x64\SerialBridge.exe"
    Write-Host "  32 bits : $publishRoot\x86\SerialBridge.exe"
    return
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'No se encontro Inno Setup 6. Instalelo con: winget install JRSoftware.InnoSetup'
}

Write-Host "==> Generando instalador (64 y 32 bits)" -ForegroundColor Cyan
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publishRoot" "/O$output" (Join-Path $root 'installer\SerialBridge.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup fallo' }

Write-Host ""
Write-Host "Listo:" -ForegroundColor Green
Write-Host "  Instalador : $output\LectorUSB-Setup-$version.exe  (64 y 32 bits)"
Write-Host "  64 bits    : $publishRoot\x64\SerialBridge.exe"
Write-Host "  32 bits    : $publishRoot\x86\SerialBridge.exe"
