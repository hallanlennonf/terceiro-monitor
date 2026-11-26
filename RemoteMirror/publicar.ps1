<#
.SYNOPSIS
    Gera o executavel unico do RemoteMirror em .\dist.

.DESCRIPTION
    Sai UM arquivo: RemoteMirror.exe, autossuficiente.
    O mesmo executavel serve para os dois lados — ao abrir, ele pergunta
    se esta maquina vai ser vista (host) ou vai ver outra (viewer).
    Quem receber nao precisa instalar .NET nem nada.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publicar.ps1
#>

[CmdletBinding()]
param(
    # Arquivo bem menor, mas exige .NET 10 Desktop Runtime no destino.
    [switch]$Leve
)

$ErrorActionPreference = 'Stop'

function Write-Step ($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok   ($m) { Write-Host "    [ok] $m" -ForegroundColor Green }

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

$args = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '/p:PublishSingleFile=true',
    '/p:IncludeNativeLibrariesForSelfExtract=true',
    '/p:DebugType=none'
)

if ($Leve) {
    $args += '--self-contained', 'false'
    Write-Host "Modo leve: o destino precisa do .NET 10 Desktop Runtime." -ForegroundColor Yellow
} else {
    $args += '--self-contained', 'true'
    $args += '/p:EnableCompressionInSingleFile=true'
}

Write-Step "Publicando o executavel unico"
$publishDir = Join-Path $dist '_publish'
dotnet publish (Join-Path $root 'src\RemoteMirror.App\RemoteMirror.App.csproj') `
    @args -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar." }

Move-Item (Join-Path $publishDir 'RemoteMirror.exe') $dist

# Em modo leve as dlls precisam acompanhar o exe.
if ($Leve) {
    Get-ChildItem $publishDir -File | Move-Item -Destination $dist -Force
}

Remove-Item $publishDir -Recurse -Force
Write-Ok "RemoteMirror.exe"

$leiame = Join-Path $root 'pacote\LEIAME.txt'
if (Test-Path $leiame) { Copy-Item $leiame $dist; Write-Ok "LEIAME.txt" }

Write-Step "Pronto"
Get-ChildItem $dist | ForEach-Object {
    Write-Host ("    {0,-28} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}

Write-Host ""
Write-Host "  Pacote em: $dist" -ForegroundColor Green
Write-Host ""
Write-Host "  Copie o RemoteMirror.exe para os dois computadores." -ForegroundColor Gray
Write-Host "  Ao abrir, ele pergunta qual e o papel de cada um." -ForegroundColor Gray
