<#
.SYNOPSIS
    Prepara ESTE computador (o notebook) para ser espelhado via RDP Shadowing,
    SEM bloquear a tela local.

.DESCRIPTION
    Rode UMA VEZ, como Administrador, no notebook.
    Depois disso o PC principal consegue abrir um espelho da sessao ativa
    com mstsc /shadow, sem desconectar nem bloquear o console fisico.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\setup-notebook.ps1
#>

[CmdletBinding()]
param(
    # Sem prompt de consentimento na tela do notebook. Use -ComConsentimento
    # se voce preferir ter que aceitar a conexao manualmente la.
    [switch]$ComConsentimento
)

$ErrorActionPreference = 'Stop'

function Write-Step  ($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok    ($m) { Write-Host "    [ok]    $m" -ForegroundColor Green }
function Write-Aviso ($m) { Write-Host "    [aviso] $m" -ForegroundColor Yellow }

# --- 0. Precisa ser admin ---------------------------------------------------
$identidade = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal  = New-Object Security.Principal.WindowsPrincipal($identidade)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Este script precisa rodar como Administrador." -ForegroundColor Red
    Write-Host "Abra o PowerShell com 'Executar como administrador' e rode de novo." -ForegroundColor Red
    exit 1
}

Write-Host "=== Setup do notebook para RDP Shadowing ===" -ForegroundColor White

# --- 1. Habilitar Remote Desktop -------------------------------------------
# O shadow nao cria sessao nova, mas o Terminal Services precisa estar ativo
# para que exista uma sessao enumeravel e espelhavel.
Write-Step "Habilitando Servicos de Area de Trabalho Remota"
$tsPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
Set-ItemProperty -Path $tsPath -Name 'fDenyTSConnections' -Value 0 -Type DWord
Write-Ok "fDenyTSConnections = 0 (RDP habilitado)"

$rdpTcp = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
if (Test-Path $rdpTcp) {
    Set-ItemProperty -Path $rdpTcp -Name 'UserAuthentication' -Value 1 -Type DWord
    Write-Ok "NLA (autenticacao em nivel de rede) ativa"
}

# --- 2. Politica de Shadow --------------------------------------------------
# Shadow = 1 -> controle total, COM consentimento do usuario local
# Shadow = 2 -> controle total, SEM consentimento   <- da o "terceiro monitor" direto
# Shadow = 3 -> somente ver,    COM consentimento
# Shadow = 4 -> somente ver,    SEM consentimento
Write-Step "Configurando a politica de Shadow"
$polPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
if (-not (Test-Path $polPath)) { New-Item -Path $polPath -Force | Out-Null }

$shadowValue = if ($ComConsentimento) { 1 } else { 2 }
Set-ItemProperty -Path $polPath -Name 'Shadow' -Value $shadowValue -Type DWord
if ($ComConsentimento) {
    Write-Ok "Shadow = 1 (controle total, pede confirmacao na tela do notebook)"
} else {
    Write-Ok "Shadow = 2 (controle total, sem prompt - conexao instantanea)"
}

# --- 3. Token de admin para conta local via rede ----------------------------
# Fora de dominio (workgroup) o Windows derruba o token de admin nas conexoes
# de rede. Sem isto o shadow responde "Acesso negado".
Write-Step "Liberando token de admin para contas locais via rede"
$sysPol = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Set-ItemProperty -Path $sysPol -Name 'LocalAccountTokenFilterPolicy' -Value 1 -Type DWord
Write-Ok "LocalAccountTokenFilterPolicy = 1"

# --- 4. Firewall ------------------------------------------------------------
Write-Step "Liberando regras de firewall"

$grupos = @(
    'Remote Desktop',
    'Area de Trabalho Remota',
    "$([char]0xC1)rea de Trabalho Remota",
    'File and Printer Sharing',
    'Compartilhamento de Arquivo e Impressora',
    'Compartilhamento de Arquivos e Impressoras',
    'Windows Management Instrumentation (WMI)',
    'Instrumentacao de Gerenciamento do Windows (WMI)'
)

$algumGrupo = $false
foreach ($g in $grupos) {
    try {
        $regras = Get-NetFirewallRule -DisplayGroup $g -ErrorAction Stop
        if ($regras) {
            Enable-NetFirewallRule -DisplayGroup $g -ErrorAction Stop
            Write-Ok "grupo liberado: $g"
            $algumGrupo = $true
        }
    } catch {
        # grupo inexistente nesse idioma de Windows - segue
    }
}
if (-not $algumGrupo) {
    Write-Aviso "Nenhum grupo de firewall conhecido foi encontrado pelo nome."
}

# Regras explicitas (idempotentes)
$nomeRegra = 'RDP Shadow (acess-remote-local)'
Get-NetFirewallRule -DisplayName $nomeRegra -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $nomeRegra -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 3389 -Profile Private, Domain | Out-Null
Write-Ok "regra criada: $nomeRegra (TCP 3389)"

# O shadow negocia por RPC dinamico; liberar o TermService cobre os casos em
# que so a 3389 nao basta.
$nomeRpc = 'RDP Shadow RPC (acess-remote-local)'
Get-NetFirewallRule -DisplayName $nomeRpc -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName $nomeRpc -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort RPC `
    -Program '%SystemRoot%\system32\svchost.exe' -Service TermService `
    -Profile Private, Domain | Out-Null
Write-Ok "regra criada: $nomeRpc (RPC dinamico do TermService)"

# --- 5. Rede precisa estar Privada ------------------------------------------
Write-Step "Verificando o perfil da rede"
foreach ($p in Get-NetConnectionProfile) {
    if ($p.NetworkCategory -eq 'Public') {
        Write-Aviso "A rede '$($p.Name)' esta como PUBLICA - o firewall vai bloquear."
        try {
            Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
            Write-Ok "rede '$($p.Name)' alterada para Privada"
        } catch {
            Write-Aviso "Nao consegui alterar. Mude em Configuracoes > Rede e Internet."
        }
    } else {
        Write-Ok "rede '$($p.Name)': $($p.NetworkCategory)"
    }
}

# --- 6. Servico ligado ------------------------------------------------------
Write-Step "Garantindo que o TermService esteja ativo"
Set-Service -Name TermService -StartupType Automatic
if ((Get-Service TermService).Status -ne 'Running') { Start-Service TermService }
Write-Ok "TermService: $((Get-Service TermService).Status)"

# --- 7. Resumo --------------------------------------------------------------
$nome = $env:COMPUTERNAME
$ips  = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress

Write-Host ""
Write-Host "=================================================" -ForegroundColor White
Write-Host " PRONTO. Dados para usar no PC principal:"          -ForegroundColor Green
Write-Host "=================================================" -ForegroundColor White
Write-Host "  Nome do computador : $nome"
Write-Host "  IP(s) na rede      : $($ips -join ', ')"
Write-Host "  Usuario logado     : $env:USERNAME"
Write-Host ""
Write-Host "  No PC principal rode:" -ForegroundColor Cyan
Write-Host "    .\conectar.ps1 -Alvo $nome" -ForegroundColor White
Write-Host ""
Write-Host "  DICA: reserve o IP do notebook no roteador (DHCP estatico)" -ForegroundColor DarkGray
Write-Host "        para o atalho nunca quebrar."                          -ForegroundColor DarkGray
Write-Host ""
Write-Aviso "Reinicie o notebook (ou faca logoff/logon) para a politica de Shadow valer."
