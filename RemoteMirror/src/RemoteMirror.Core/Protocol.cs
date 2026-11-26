namespace RemoteMirror.Core;

/// <summary>
/// Constantes compartilhadas entre host e viewer.
/// </summary>
public static class Protocol
{
    /// <summary>Assinatura trocada no inicio do handshake.</summary>
    public static readonly byte[] Magic = "RMIR"u8.ToArray();

    /// <summary>Versao do protocolo. Host e viewer precisam bater.</summary>
    public const byte Version = 1;

    /// <summary>Porta TCP padrao.</summary>
    public const int DefaultPort = 7701;

    /// <summary>Lado do quadro usado para fatiar a tela (pixels).</summary>
    public const int TileSize = 128;

    /// <summary>Teto de tamanho de mensagem, para nao estourar memoria com lixo.</summary>
    public const int MaxMessageSize = 64 * 1024 * 1024;

    /// <summary>Iteracoes do PBKDF2 na derivacao da chave a partir da senha.</summary>
    public const int KdfIterations = 200_000;
}

/// <summary>Mensagens que o host envia para o viewer.</summary>
public enum ServerMessage : byte
{
    /// <summary>Lista de monitores e qual esta selecionado.</summary>
    ServerInfo = 1,

    /// <summary>Quadro completo: todos os tiles. Enviado ao conectar e sob demanda.</summary>
    Keyframe = 2,

    /// <summary>Quadro parcial: apenas os tiles que mudaram.</summary>
    Tiles = 3,

    /// <summary>Resposta ao Ping, para medir latencia.</summary>
    Pong = 4,

    /// <summary>Posicao atual do cursor no monitor capturado.</summary>
    Cursor = 5,

    /// <summary>Host recusou algo (ex.: controle desabilitado) ou houve erro.</summary>
    Notice = 6,

    /// <summary>
    /// O cursor remoto encostou na borda e esta saindo: o PC deve retomar o
    /// controle local. E o que fecha o ciclo do "terceiro monitor".
    /// </summary>
    EdgeExit = 7,

    /// <summary>Texto copiado no host, para espelhar na area de transferencia do PC.</summary>
    ClipboardText = 8,
}

/// <summary>Mensagens que o viewer envia para o host.</summary>
public enum ClientMessage : byte
{
    /// <summary>Pede um quadro completo (apos redimensionar, ou se dessincronizou).</summary>
    RequestKeyframe = 20,

    /// <summary>Troca o monitor capturado.</summary>
    SelectMonitor = 21,

    /// <summary>Move o mouse para (x, y) em coordenadas do quadro.</summary>
    MouseMove = 22,

    /// <summary>Pressiona ou solta um botao do mouse.</summary>
    MouseButton = 23,

    /// <summary>Roda do mouse.</summary>
    MouseWheel = 24,

    /// <summary>Pressiona ou solta uma tecla.</summary>
    KeyEvent = 25,

    /// <summary>Medicao de latencia.</summary>
    Ping = 26,

    /// <summary>Ajusta qualidade do JPEG e taxa de quadros.</summary>
    SetQuality = 27,

    /// <summary>
    /// Movimento relativo do mouse (dx, dy).
    /// O modo compartilhado nao pode usar coordenadas absolutas: o cursor do
    /// host tem vida propria e so precisa ser empurrado.
    /// </summary>
    MouseMoveRelative = 28,

    /// <summary>
    /// O cursor acabou de entrar no host vindo de uma borda.
    /// Manda em que altura relativa (0.0 topo, 1.0 base) ele entrou, para o
    /// cursor aparecer la na mesma altura em que saiu daqui.
    /// </summary>
    EdgeEnter = 29,

    /// <summary>O PC retomou o controle; o host pode parar de receber input.</summary>
    EdgeLeave = 30,

    /// <summary>Texto copiado no PC, para espelhar na area de transferencia do host.</summary>
    ClipboardText = 31,

    /// <summary>
    /// Liga ou desliga o envio de video para este cliente.
    /// No modo "terceiro monitor" a tela remota e olhada ao vivo, entao
    /// transmitir imagem seria desperdicio puro de banda e CPU.
    /// </summary>
    SetVideoEnabled = 32,
}

/// <summary>De que lado o cursor atravessa para chegar no host.</summary>
public enum ScreenEdge : byte
{
    /// <summary>O host esta a direita do PC (caso mais comum: notebook na mesa, a direita).</summary>
    Right = 0,

    /// <summary>O host esta a esquerda do PC.</summary>
    Left = 1,

    /// <summary>O host esta acima do PC.</summary>
    Top = 2,

    /// <summary>O host esta abaixo do PC.</summary>
    Bottom = 3,
}

/// <summary>Botoes de mouse no protocolo.</summary>
public enum MouseButtonCode : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
}
