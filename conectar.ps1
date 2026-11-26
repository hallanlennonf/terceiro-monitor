<#
.SYNOPSIS
    Abre um espelho da tela do notebook (RDP Shadowing) sem bloquear a tela dele.

.DESCRIPTION
    Rode no PC principal. Descobre sozinho o ID da sessao ativa no alvo e
    dispara o mstsc em modo shadow. A tela do notebook continua ligada,
    desbloqueada e usavel - voce so ganha um espelho dela aqui.

.PARAMETER Alvo
    Nome do notebook na rede (ou IP). Ex: -Alvo NOME-DO-NOTEBOOK

.PARAMETER SomenteVer
    Abre em modo leitura: voce ve, mas nao controla mouse/teclado.

.PARAMETER Janela
    Abre em janela ao inves de tela cheia.

.PARAMETER SessaoId
    Forca um ID de sessao especifico (pula a deteccao automatica).

.EXAMPLE
    .\conectar.ps1 -Alvo NOME-DO-NOTEBOOK

.EXAMPLE
    .\conectar.ps1 -Alvo 192.168.0.42 -SomenteVer -Janela
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Alvo,

    [switch]$SomenteVer,
    [switch]$Janela,
    [int]$SessaoId = -1
)

$ErrorActionPreference = 'Stop'

function Write-Step  ($m) { Write-Host "==> $m"      -ForegroundColor Cyan }
function Write-Ok    ($m) { Write-Host "    [ok]    $m" -ForegroundColor Green }
function Write-Aviso ($m) { Write-Host "    [aviso] $m" -ForegroundColor Yellow }
function Write-Erro  ($m) { Write-Host "    [erro]  $m" -ForegroundColor Red }

# --- 1. O alvo esta de pe? --------------------------------------------------
Write-Step "Procurando $Alvo na rede"
if (-not (Test-Connection -ComputerName $Alvo -Count 1 -Quiet)) {
    Write-Aviso "Nao respondeu ao ping (pode ser so o firewall bloqueando ICMP)."
} else {
    Write-Ok "$Alvo respondeu"
}

# --- 2. Descobrir a sessao ativa -------------------------------------------
function Get-SessaoAtiva {
    param([string]$Computador)

    $saida = & qwinsta.exe /server:$Computador 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "qwinsta falhou: $($saida -join ' ')"
    }

    $encontradas = @()
    foreach ($linha in $saida) {
        $texto = [string]$linha
        # Formato: SESSIONNAME  USERNAME  ID  STATE  TYPE  DEVICE
        if ($texto -match '^\s*>?\s*(\S+)?\s+(\S+)\s+(\d+)\s+(Active|Ativo)\b') {
            $encontradas += [pscustomobject]@{
                Sessao  = $matches[1]
                Usuario = $matches[2]
                Id      = [int]$matches[3]
            }
        }
    }
    return $encontradas
}

if ($SessaoId -ge 0) {
    Write-Ok "Usando sessao informada: $SessaoId"
} else {
    Write-Step "Descobrindo a sessao ativa no alvo"
    try {
        $sessoes = Get-SessaoAtiva -Computador $Alvo

        if ($sessoes.Count -eq 0) {
            Write-Aviso "Nenhuma sessao ativa encontrada. Tentando o padrao (ID 1)."
            Write-Aviso "Confirme que alguem esta logado no notebook."
            $SessaoId = 1
        }
        elseif ($sessoes.Count -eq 1) {
            $SessaoId = $sessoes[0].Id
            Write-Ok "Sessao $SessaoId - usuario '$($sessoes[0].Usuario)'"
        }
        else {
            Write-Host "    Mais de uma sessao ativa:" -ForegroundColor White
            foreach ($s in $sessoes) {
                Write-Host "      [$($s.Id)] $($s.Usuario)  ($($s.Sessao))"
            }
            $escolha = Read-Host "    Qual ID"
            $SessaoId = [int]$escolha
        }
    }
    catch {
        Write-Aviso "Nao consegui enumerar as sessoes: $($_.Exception.Message)"
        Write-Aviso "Caindo para o ID 1 (o mais comum do console fisico)."
        $SessaoId = 1
    }
}

# --- 3. Montar e disparar o mstsc ------------------------------------------
$argumentos = @(
    "/shadow:$SessaoId"
    "/v:$Alvo"
    '/noConsentPrompt'
)

if (-not $SomenteVer) { $argumentos += '/control' }
if (-not $Janela)     { $argumentos += '/f' }

$modo = if ($SomenteVer) { 'somente visualizar' } else { 'com controle' }
$tela = if ($Janela)     { 'em janela' }         else { 'tela cheia' }

Write-Step "Abrindo espelho da sessao $SessaoId em $Alvo ($modo, $tela)"
Write-Host "    mstsc.exe $($argumentos -join ' ')" -ForegroundColor DarkGray

$proc = Start-Process -FilePath 'mstsc.exe' -ArgumentList $argumentos -PassThru
Start-Sleep -Seconds 3

if ($proc.HasExited -and $proc.ExitCode -ne 0) {
    Write-Host ""
    Write-Erro "O mstsc fechou sozinho (codigo $($proc.ExitCode))."
    Write-Host ""
    Write-Host "  Checklist rapido:" -ForegroundColor Yellow
    Write-Host "   1. Rodou o setup-notebook.ps1 como admin no notebook?"
    Write-Host "   2. Reiniciou o notebook depois do setup?"
    Write-Host "   3. Sua conta e Administrador LA no notebook?"
    Write-Host "   4. Mesmo usuario/senha nos dois PCs? Se nao, salve a credencial:"
    Write-Host "        cmdkey /generic:TERMSRV/$Alvo /user:USUARIO /pass" -ForegroundColor White
    Write-Host "   5. Tem alguem logado no console do notebook (tela destravada)?"
    exit 1
}

Write-Ok "Espelho aberto. A tela do notebook continua ligada e livre."
Write-Host ""
Write-Host "  Para sair do modo tela cheia: Ctrl+Alt+Break" -ForegroundColor DarkGray
Write-Host "  Para encerrar o espelho:      feche a janela ou Ctrl+Alt+Break e fechar" -ForegroundColor DarkGray
