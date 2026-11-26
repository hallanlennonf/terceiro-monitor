# RemoteMirror

Acesso remoto local em C# que **não bloqueia a tela da máquina acessada**.

Resolve o problema do RDP do Windows: ele só permite uma sessão interativa, então
ao conectar ele desconecta o console físico e o notebook mostra a tela de bloqueio.
O RemoteMirror espelha a sessão que já está rodando — a tela de lá continua ligada,
desbloqueada e em uso, e você ganha um monitor a mais.

Feito para rede local e para distribuir: dois executáveis, sem servidor no meio,
sem conta, sem instalação de runtime.

## Como funciona

**Captura** — Desktop Duplication API (DXGI). É a via que o próprio Windows usa
para espelhar telas: lê o framebuffer já composto pela GPU. Não cria sessão, não
instala driver de vídeo virtual, não bloqueia nada.

**Transporte** — a tela é fatiada num grid de 128×128. A cada quadro o host
compara o hash de cada tile com o anterior e manda só os que mudaram, em JPEG.
Um desktop parado gera zero tráfego; digitar num editor suja 2 ou 3 tiles.

**Entrada** — `SendInput`, que injeta na fila da sessão como se viesse do hardware.
Por isso o teclado e o mouse de quem está na máquina continuam funcionando ao
mesmo tempo: os eventos se misturam em vez de um tomar o lugar do outro.

**Segurança** — AES-256-GCM com chave derivada da senha por PBKDF2 (200k iterações).
A senha não trafega: os dois lados provam que a conhecem por desafio-resposta mútuo.
Contador por direção impede replay. A senha em disco fica protegida por DPAPI.

**Descoberta** — o host responde a um broadcast UDP com nome e porta, então o
viewer lista as máquinas da rede sozinho. O colega não precisa saber IP nenhum.

## Medições nesta máquina

Teste de ponta a ponta contra um host real (`tests/RemoteMirror.SmokeTest`),
1920×1080, loopback:

| | |
|---|---|
| Custo de captura por quadro | 5,66 ms mínimo (≈175 fps de teto) |
| Keyframe inicial | 135 tiles, ~260 KB |
| Tráfego em uso normal | ~145 KB/s |
| Latência ida e volta | mediana 0,3 ms (máx 1,1 ms) |
| Senha errada | recusada no handshake |

Loopback não tem rede no meio: numa LAN cabeada some a latência do switch
(tipicamente 1–3 ms). O custo de captura e o volume de tráfego valem igual.

## Compilar

Precisa do .NET 10 SDK.

```powershell
dotnet build
```

Gerar o executável para distribuir:

```powershell
powershell -ExecutionPolicy Bypass -File .\publicar.ps1
```

Sai **um arquivo só** em `dist\`: `RemoteMirror.exe`, ~47 MB, autossuficiente —
quem receber não precisa instalar .NET. Para um arquivo pequeno (exigindo .NET 10
Desktop Runtime no destino), use `-Leve`.

## Usar

O mesmo executável serve para os dois lados. Copie para os dois computadores;
ao abrir, ele pergunta o papel de cada um:

- **"Ser visto por outro computador"** — instala, libera o firewall, põe para
  iniciar com o Windows e deixa o ícone na bandeja.
- **"Ver a tela de outro computador"** — abre o viewer. Não instala nada.

A senha fica no menu da bandeja do lado que é visto (clique em *Senha* para copiar).

Atalhos na sessão: `Ctrl+Alt+F` tela cheia · `Ctrl+Alt+C` controlar/só olhar ·
`Ctrl+Alt+M` trocar monitor · `Ctrl+Alt+Q` sair. Todo o resto do teclado vai
para a máquina remota, Alt+Tab e Tab inclusive.

### Linha de comando

```
RemoteMirror.exe                 pergunta o que fazer
RemoteMirror.exe --host          sobe o host direto na bandeja
RemoteMirror.exe --viewer        abre o viewer
RemoteMirror.exe --viewer IP     conecta direto (senha já salva)
RemoteMirror.exe --install       instala o host sem perguntar
RemoteMirror.exe --uninstall     remove deste computador
```

A inicialização do Windows usa `--host`, então o host sobe direto, sem tela
de escolha.

## Estrutura

```
src/RemoteMirror.App       executável único: tela de escolha e instalador embutido
src/RemoteMirror.Core      protocolo, canal cifrado, descoberta UDP
src/RemoteMirror.Host      captura DXGI, codificador de tiles, SendInput, bandeja
src/RemoteMirror.Viewer    conexão, render, captura de entrada
tests/RemoteMirror.SmokeTest   cliente headless que valida o pipeline completo
pacote/                    LEIAME que acompanha o dist
```

Host e Viewer são bibliotecas; só o App vira executável.

## Limitações

- O host roda sem elevação. Por regra do Windows (UIPI), ele não injeta teclas
  nem cliques em janelas de processos elevados. Rode o host como administrador
  se precisar disso.
- Tela de bloqueio e Ctrl+Alt+Del não são capturadas — são áreas protegidas do
  Windows, nenhum programa comum as vê.
- Jogos em tela cheia exclusiva derrubam a duplicação; ela se recupera sozinha
  em alguns segundos.
- É espelho, não sessão separada: quem está na máquina vê seu cursor mexer.
- Pensado para LAN. Não exponha a porta na internet.

## Verificado e não verificado

Validado de ponta a ponta contra o executável único publicado: descoberta UDP,
recusa de senha errada, handshake cifrado, keyframe, quadros parciais,
reconstrução correta da imagem (conferida a olho). A tela de escolha, o modo
`--host` e o modo `--viewer` abrem corretamente.

**Não testado ainda:**

- `--install` / `--uninstall` — a execução foi barrada pelo controle de
  permissões do ambiente, por mexer em firewall e inicialização.
- conexão entre duas máquinas de verdade pela rede;
- injeção de mouse e teclado;
- troca de monitor pelo viewer.

Os três últimos precisam das duas pontas rodando.
