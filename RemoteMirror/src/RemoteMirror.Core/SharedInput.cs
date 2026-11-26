namespace RemoteMirror.Core;

/// <summary>Retangulo do desktop, em coordenadas de tela.</summary>
public readonly record struct DesktopBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Decide quando o cursor deve "sair" do host e voltar para o PC.
///
/// Logica pura de proposito: sem cursor real, sem Win32, para ser testavel.
/// A regra e a de um monitor de verdade: so sai quando o cursor ja esta
/// encostado na borda de entrada E o usuario continua empurrando naquela
/// direcao. Um unico movimento que passa do limite nao basta, senao o cursor
/// escaparia toda vez que alguem mira num botao da lateral.
/// </summary>
public sealed class EdgeExitDetector(ScreenEdge pcSideEdge, int threshold = 24)
{
    /// <summary>
    /// Movimento contrario que ainda nao conta como desistencia.
    ///
    /// Encostado na borda, a posicao relatada pelo Windows oscila em torno do
    /// ponto de parada: um mesmo gesto continuo aparece como +1, -1, +1, -1.
    /// Sem esta folga, cada -1 zerava a pressao e a travessia nunca
    /// acontecia, por mais que a pessoa empurrasse.
    /// </summary>
    private const int JitterTolerance = 4;

    /// <summary>
    /// Prazo da pressao acumulada, em milissegundos.
    ///
    /// Sem prazo, a folga acima vira uma porta aberta: o ruido de +1 do cursor
    /// parado na borda soma devagarinho ate passar do limite, e o cursor
    /// atravessa sozinho so porque ficou descansando ali. Um empurrao de
    /// verdade entrega o limite inteiro dentro de um gesto; ruido nao.
    /// </summary>
    private const long PressureLifetimeMs = 250;

    /// <summary>De que lado o cursor entrou NO HOST (oposto da borda do PC).</summary>
    public ScreenEdge HostEntrySide { get; } = Opposite(pcSideEdge);

    private int _pressure;
    private int _away;
    private long _lastPushMs = -1;

    /// <summary>Quanto o usuario ja empurrou contra a borda, em pixels.</summary>
    public int Pressure => _pressure;

    public void Reset()
    {
        _pressure = 0;
        _away = 0;
        _lastPushMs = -1;
    }

    /// <summary>
    /// Processa um movimento ja aplicado.
    /// </summary>
    /// <param name="dx">Deslocamento pedido no eixo X.</param>
    /// <param name="dy">Deslocamento pedido no eixo Y.</param>
    /// <param name="x">Posicao do cursor DEPOIS do movimento.</param>
    /// <param name="y">Posicao do cursor DEPOIS do movimento.</param>
    /// <param name="bounds">Limites do desktop do host.</param>
    /// <param name="nowMs">
    /// Relogio em milissegundos, para dar prazo a pressao acumulada. Passe -1
    /// (o padrao) para desligar o prazo, como nos testes que raciocinam so
    /// sobre a sequencia de movimentos.
    /// </param>
    /// <returns>true quando o controle deve voltar para o PC.</returns>
    public bool Feed(int dx, int dy, int x, int y, DesktopBounds bounds, long nowMs = -1)
    {
        // Pressao vencida nao conta: ruido espacado nunca soma ate o limite,
        // so um gesto continuo soma.
        if (nowMs >= 0 && _lastPushMs >= 0 && nowMs - _lastPushMs > PressureLifetimeMs)
        {
            _pressure = 0;
            _away = 0;
        }

        // "Empurrando" = movendo na direcao da borda de entrada,
        // com o cursor ja grudado nela.
        var (pushing, atEdge) = HostEntrySide switch
        {
            ScreenEdge.Left => (dx < 0, x <= bounds.Left),
            ScreenEdge.Right => (dx > 0, x >= bounds.Right - 1),
            ScreenEdge.Top => (dy < 0, y <= bounds.Top),
            ScreenEdge.Bottom => (dy > 0, y >= bounds.Bottom - 1),
            _ => (false, false),
        };

        var magnitude = HostEntrySide is ScreenEdge.Left or ScreenEdge.Right
            ? Math.Abs(dx)
            : Math.Abs(dy);

        if (pushing && atEdge)
        {
            _away = 0;
            _pressure += magnitude;
            _lastPushMs = nowMs;

            if (_pressure >= threshold)
            {
                Reset();
                return true;
            }

            return false;
        }

        // Movimento para longe da borda significa que a pessoa desistiu - mas
        // so depois de passar da folga, para nao confundir oscilacao com
        // desistencia.
        var movingAway = HostEntrySide switch
        {
            ScreenEdge.Left => dx > 0,
            ScreenEdge.Right => dx < 0,
            ScreenEdge.Top => dy > 0,
            ScreenEdge.Bottom => dy < 0,
            _ => false,
        };

        if (movingAway)
        {
            _away += magnitude;
            if (_away > JitterTolerance) Reset();
        }

        return false;
    }

    /// <summary>
    /// Onde o cursor deve aparecer no host ao entrar.
    /// Se o host esta a direita do PC, o cursor entra pela esquerda dele,
    /// na mesma altura proporcional em que saiu daqui.
    /// </summary>
    public (int X, int Y) EntryPoint(DesktopBounds bounds, double relativePosition)
    {
        var rel = Math.Clamp(relativePosition, 0.0, 1.0);

        return HostEntrySide switch
        {
            ScreenEdge.Left => (bounds.Left + 1, bounds.Top + (int)(bounds.Height * rel)),
            ScreenEdge.Right => (bounds.Right - 2, bounds.Top + (int)(bounds.Height * rel)),
            ScreenEdge.Top => (bounds.Left + (int)(bounds.Width * rel), bounds.Top + 1),
            ScreenEdge.Bottom => (bounds.Left + (int)(bounds.Width * rel), bounds.Bottom - 2),
            _ => (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
        };
    }

    /// <summary>
    /// Detector para o lado do PC: aqui a borda informada e aquela contra a
    /// qual o usuario empurra o cursor para ATRAVESSAR, nao a de volta.
    /// </summary>
    public static EdgeExitDetector PushingAgainst(ScreenEdge edge, int threshold = 24) =>
        new(Opposite(edge), threshold);

    private static ScreenEdge Opposite(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Right => ScreenEdge.Left,
        ScreenEdge.Left => ScreenEdge.Right,
        ScreenEdge.Top => ScreenEdge.Bottom,
        ScreenEdge.Bottom => ScreenEdge.Top,
        _ => ScreenEdge.Left,
    };
}
