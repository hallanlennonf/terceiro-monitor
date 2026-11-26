# Terceiro monitor — acesso remoto local sem bloquear a tela

Usar o notebook da rede local como monitor a mais do PC principal, **sem
desconectar nem bloquear a tela dele**. Dois caminhos: um app proprio de um
arquivo so (abaixo) e o RDP Shadowing nativo do Windows, sem instalar nada.

## Baixar

O jeito mais rapido de usar: **[baixe o RemoteMirror.exe na pagina de releases](https://github.com/hallanlennonf/terceiro-monitor/releases/latest)**.

E um arquivo so, 47 MB, sem instalar runtime nem criar conta. Abra nos dois
computadores e ele pergunta qual e o papel de cada um:

- **no notebook** (o que vai ser visto): *Instalar e ligar* — fica na bandeja e
  sobe com o Windows;
- **no PC** (o que vai ver): *Terceiro monitor* — o cursor passa pela borda da
  tela e cai no notebook, sem video nenhum na rede.

Detalhes, modos e diagnostico em [RemoteMirror/README.md](RemoteMirror/README.md).

### Se o antivirus acusar o executavel

Pode acontecer, e e previsivel. O RemoteMirror **faz as mesmas coisas que um
trojan de acesso remoto faz**, porque e para isso que ele serve:

- instala um gancho de teclado de baixo nivel (`SetWindowsHookEx`), que e a
  tecnica de um keylogger;
- captura a tela continuamente (Desktop Duplication API);
- injeta cliques e teclas na outra maquina (`SendInput`);
- abre uma porta e aceita conexao pela rede;
- se copia para `%LOCALAPPDATA%` e se registra para subir com o Windows.

Um antivirus que olha comportamento e marca isso nao esta errado nem quebrado:
essa lista, junta, e a definicao de um RAT. A diferenca entre esta ferramenta e
um trojan nao esta no que ela faz, e sim em **quem a instalou e quem tem a
senha** - e isso nenhum antivirus tem como saber. Somado a um executavel
unico, que se descompacta sozinho, sem assinatura de codigo e sem reputacao
nenhuma, a deteccao e quase garantida em alguns produtos.

O que da para fazer:

1. **Confira o que voce baixou.** O `RemoteMirror.exe` da release v1.0.0 tem
   este SHA-256:

   ```
   759d7020684c24450d0c3f0c9b94b7e02138212a591be63800a963f583f016e2
   ```

   No PowerShell: `Get-FileHash .\RemoteMirror.exe`. Se bater, o arquivo e o
   mesmo que foi publicado aqui. Se **nao** bater, nao execute - aí e outra
   conversa, e nao e sobre antivirus.

2. **Compile do codigo-fonte.** Esta tudo neste repositorio: `dotnet build` e
   `publicar.ps1`. E a unica forma de nao depender da confianca de ninguem.

3. **Nao desligue o antivirus.** Se decidir usar mesmo assim, crie uma excecao
   apenas para esse arquivo, e so se voce mesmo o baixou daqui.

O honesto a dizer para quem voce for passar o programa: isto e uma ferramenta
de acesso remoto, o antivirus vai reclamar, e a reclamacao faz sentido.

O resto desta pagina descreve o **caminho sem instalar nada**, usando RDP
Shadowing nativo do Windows — util quando as duas maquinas sao suas e voce
prefere nao copiar executavel.

## Por que o RDP normal não serve

O Windows Pro só permite **uma sessão interativa**. Ao entrar por RDP comum, ele
*desconecta* o console físico — por isso o notebook mostra a tela de bloqueio.

O **modo shadow** não cria sessão nova: ele **espelha a sessão que já está
rodando no console**. Resultado: a tela do notebook continua ligada, desbloqueada
e visível, e você vê o mesmo desktop aqui. Exatamente o "terceiro monitor".

| | RDP normal | AnyDesk | **RDP Shadow** |
|---|---|---|---|
| Bloqueia a tela local | sim | não | **não** |
| Velocidade em LAN | ótima | média | **ótima** |
| Precisa instalar | não | sim | **não** |
| Passa por servidor externo | não | sim | **não** |

## Instalação

### 1. No notebook (uma vez só, como Administrador)

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-notebook.ps1
```

O script:
- habilita os Serviços de Área de Trabalho Remota
- define a política `Shadow = 2` (controle total, sem prompt de confirmação)
- define `LocalAccountTokenFilterPolicy = 1` (necessário fora de domínio)
- libera as regras de firewall (3389 + RPC do TermService)
- muda o perfil da rede para Privada, se estiver como Pública
- imprime o nome e o IP do notebook no final

Prefere confirmar a conexão manualmente na tela do notebook? Use
`-ComConsentimento` (política `Shadow = 1`).

**Reinicie o notebook depois** — a política de Shadow só vale após o reinício.

### 2. No PC principal

```powershell
.\conectar.ps1 -Alvo NOME-DO-NOTEBOOK
```

O script descobre sozinho o ID da sessão ativa e abre o espelho em tela cheia
com controle de mouse e teclado.

Variações:

```powershell
.\conectar.ps1 -Alvo NOME-DO-NOTEBOOK -SomenteVer     # só olhar, sem controlar
.\conectar.ps1 -Alvo NOME-DO-NOTEBOOK -Janela         # em janela, não tela cheia
.\conectar.ps1 -Alvo 192.168.0.42 -SessaoId 1    # forçando IP e sessão
```

### 3. Atalho de um clique

Edite a linha `set ALVO=` no `terceiro-monitor.cmd`, depois mande um atalho dele
para a área de trabalho ou para a barra de tarefas. Daí é um clique e o espelho
abre.

## Uso no dia a dia

- **Sair da tela cheia:** `Ctrl+Alt+Break`
- **Encerrar o espelho:** feche a janela — a sessão do notebook continua
  rodando normalmente, nada é deslogado.
- Quem estiver no notebook vê seu cursor se mexer (é um espelho, não uma
  sessão separada). Como a máquina é sua, isso é só um detalhe.

## Credenciais

O shadow usa sua conta do PC principal para autenticar no notebook. Ela precisa
ser **Administrador no notebook**.

Se o usuário/senha forem diferentes nas duas máquinas, salve a credencial uma vez:

```powershell
cmdkey /generic:TERMSRV/NOME-DO-NOTEBOOK /user:USUARIO-DO-NOTEBOOK /pass
```

## Se der errado

| Sintoma | Causa provável |
|---|---|
| "Acesso negado" | Sua conta não é admin no notebook, ou faltou `LocalAccountTokenFilterPolicy` + reinício |
| "O computador não pôde ser encontrado" | Nome não resolve na rede — use o IP direto |
| "A sessão não existe" | Ninguém logado no notebook, ou ID errado — rode `qwinsta /server:ALVO` |
| mstsc abre e fecha na hora | Política de Shadow não aplicada — reinicie o notebook |
| Nada conecta | Rede marcada como Pública no notebook, ou firewall de terceiros |

Diagnóstico manual a partir do PC principal:

```powershell
qwinsta /server:NOME-DO-NOTEBOOK
```

Se isso listar as sessões, a comunicação está ok e o problema é só a política
de shadow. Se falhar, é rede/firewall/credencial.

## Plano B

Se por algum motivo o shadow não rolar, o caminho alternativo é
**Sunshine (host, no notebook) + Moonlight (cliente, no PC)**: streaming de
desktop com encode por GPU, ~5–15 ms em LAN, open source, e também não bloqueia
nem desconecta a tela local.

## Arquivos

| Arquivo | Onde roda | O que faz |
|---|---|---|
| `setup-notebook.ps1` | notebook, 1x, admin | prepara o host para shadowing |
| `conectar.ps1` | PC principal | abre o espelho |
| `terceiro-monitor.cmd` | PC principal | atalho de um clique |

---

## Opção 2 — RemoteMirror (app próprio, C#)

Os scripts acima resolvem para você, nesta máquina. Para **distribuir aos colegas**,
há um app em [`RemoteMirror/`](RemoteMirror/): **um executável só**, que ao abrir
pergunta se a máquina vai ser vista ou vai ver outra, e se instala sozinho.
Descoberta automática na rede, senha, conexão cifrada e ícone na bandeja — sem
depender de política de GPO nem de shadow.

Veja [RemoteMirror/README.md](RemoteMirror/README.md).
