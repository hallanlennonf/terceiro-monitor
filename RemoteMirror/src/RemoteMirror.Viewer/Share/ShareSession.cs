using System.Runtime.InteropServices;
using RemoteMirror.Core;

namespace RemoteMirror.Viewer.Share;

/// <summary>Onde o cursor esta agora.</summary>
public enum CursorSide
{
    /// <summary>No PC, uso normal.</summary>
    Local,

    /// <summary>Do lado do notebook: tudo que o usuario faz vai para la.</summary>
    Remote,
}

/// <summary>
/// O "terceiro monitor": faz o cursor e o teclado atravessarem a borda da tela.
///
/// Regras de seguranca que moldaram este codigo — um bug aqui deixa a pessoa
/// sem mouse e sem teclado na propria maquina:
///   - queda de conexao devolve o controle na hora;
///   - Ctrl+Alt+Home devolve o controle sempre, sem depender da rede;
///   - qualquer excecao no caminho devolve o controle.
/// </summary>
public sealed class ShareSession : IDisposable
{
    private readonly ViewerClient _client;
    private readonly InputHooks _hooks = new();
    private readonly SynchronizationContext _ui;

    private RawMouseDeltas? _raw;

    private bool _ctrlDown;
    private bool _altDown;

    private double _remainderX;
    private double _remainderY;

    private int _exitX;
    private int _exitY;
    private bool _hasExit;

    /// <summary>
    /// Multiplicador do movimento enviado ao notebook. 1,0 envia o
    /// deslocamento do mouse como ele vem do dispositivo.
    /// </summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>
    /// Teto do deslocamento aceito em uma unica leitura do mouse.
    ///
    /// Um mouse rapido de alta resolucao chega a algumas dezenas de unidades
    /// por leitura; centenas nao vem de gesto nenhum.
    /// </summary>
    private const int MaxDeltaPerEvent = 250;

    private EdgeExitDetector _crossDetector;
    private ScreenEdge _edge;
    private CursorHider? _hider;

    private double _lastRelative = 0.5;
    private bool _disposed;

    /// <summary>Mudou de lado.</summary>
    public event Action<CursorSide>? SideChanged;

    /// <summary>Algo digno de aparecer no menu / log.</summary>
    public event Action<string>? Status;

    public CursorSide Side { get; private set; } = CursorSide.Local;

    /// <summary>Por qual borda do PC o cursor atravessa para o notebook.</summary>
    public ScreenEdge Edge
    {
        get => _edge;
        set
        {
            _edge = value;
            _crossDetector = EdgeExitDetector.PushingAgainst(value);
        }
    }

    /// <summary>Espelhar a area de transferencia ao atravessar.</summary>
    public bool ShareClipboard { get; set; } = true;

    /// <summary>Esconder o cursor do PC enquanto ele esta do lado de la.</summary>
    public bool HideLocalCursor { get; set; } = true;

    /// <summary>
    /// Onde a tela do notebook comeca e quanto ela ocupa, em fracao da altura
    /// do desktop do PC. Vem da telinha de posicionamento.
    ///
    /// Sem isto o cursor sempre entraria proporcionalmente a altura toda do
    /// PC, e num notebook mais baixo que o monitor ele apareceria deslocado.
    /// </summary>
    public double SpanStart { get; set; }

    public double SpanLength { get; set; } = 1.0;

    /// <summary>
    /// Converte a altura em que o cursor saiu daqui para a altura
    /// correspondente na tela do notebook.
    /// </summary>
    public double MapToRemote(double localRelative)
    {
        if (SpanLength <= 0.0001) return 0.5;
        return Math.Clamp((localRelative - SpanStart) / SpanLength, 0.0, 1.0);
    }

    public ShareSession(ViewerClient client, ScreenEdge edge)
    {
        _client = client;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _edge = edge;
        _crossDetector = EdgeExitDetector.PushingAgainst(edge);

        _hooks.Mouse += OnMouse;
        _hooks.Key += OnKey;

        _client.EdgeExit += () => _ui.Post(_ => ReturnToLocal("o notebook devolveu na borda"), null);
        _client.Disconnected += _ => _ui.Post(_ => ReturnToLocal("a conexao caiu"), null);
        _client.ClipboardText += text => _ui.Post(_ => ApplyClipboard(text), null);
    }

    /// <summary>Liga os hooks e passa a vigiar a borda.</summary>
    public void Start()
    {
        _hooks.Install();

        _raw ??= new RawMouseDeltas();
        _raw.Moved += OnRawMove;

        // Video so gastaria banda: a tela do notebook e olhada ao vivo.
        _ = _client.SetVideoEnabledAsync(false);

        Status?.Invoke($"Vigiando a borda {Describe(_edge)}.");
    }

    public void Stop()
    {
        if (Side == CursorSide.Remote) ReturnToLocal("modo desligado");

        if (_raw is not null) _raw.Moved -= OnRawMove;
        _hooks.Uninstall();
    }

    // --------------------------------------------------------------- mouse

    /// <summary>
    /// Ajusta o deslocamento a sensibilidade escolhida, guardando a sobra.
    ///
    /// O deslocamento bruto do mouse nao passa pela curva de aceleracao que o
    /// Windows aplica ao cursor daqui, entao o movimento chega mais "duro" do
    /// lado de la. Em vez de imitar a curva do Windows - que muda com as
    /// configuracoes de cada maquina - fica um fator simples e ajustavel.
    ///
    /// A sobra importa: multiplicar por 0,6 e truncar jogaria todo movimento
    /// lento para zero, e o cursor pararia de responder a toques finos.
    /// </summary>
    private (int, int) ApplySensitivity(int dx, int dy)
    {
        // Nenhum gesto humano salta isto em uma unica leitura do mouse. Um
        // valor maior e defeito - leitura corrompida, varios eventos somados
        // por atraso, ou dispositivo reportando besteira - e deixar passar
        // joga o cursor para o outro lado da tela de la.
        dx = Math.Clamp(dx, -MaxDeltaPerEvent, MaxDeltaPerEvent);
        dy = Math.Clamp(dy, -MaxDeltaPerEvent, MaxDeltaPerEvent);

        if (Math.Abs(Sensitivity - 1.0) < 0.001) return (dx, dy);

        var fx = dx * Sensitivity + _remainderX;
        var fy = dy * Sensitivity + _remainderY;

        var ix = (int)Math.Truncate(fx);
        var iy = (int)Math.Truncate(fy);

        _remainderX = fx - ix;
        _remainderY = fy - iy;

        return (ix, iy);
    }

    /// <summary>
    /// Deslocamento fisico do mouse, vindo do dispositivo.
    ///
    /// A posicao do cursor nao serve para isto: na borda ela para de
    /// acompanhar o mouse e passa a oscilar, de modo que a diferenca entre
    /// leituras alterna de sinal e nenhum empurrao se acumula. O movimento
    /// bruto continua chegando limpo.
    /// </summary>
    private void OnRawMove(int dx, int dy)
    {
        (dx, dy) = ApplySensitivity(dx, dy);
        if (dx == 0 && dy == 0) return;

        if (Side == CursorSide.Remote)
        {
            try
            {
                _ = _client.MouseMoveRelativeAsync(dx, dy);
            }
            catch (Exception ex)
            {
                ReturnToLocal($"erro ao enviar o mouse: {ex.Message}");
            }

            return;
        }

        var bounds = VirtualBounds();
        var pos = Cursor.Position;

        if (!_crossDetector.Feed(dx, dy, pos.X, pos.Y, bounds, Environment.TickCount64)) return;

        _lastRelative = bounds.Height <= 1
            ? 0.5
            : Math.Clamp((pos.Y - bounds.Top) / (double)bounds.Height, 0, 1);

        // Fora da faixa que a tela do notebook ocupa, a travessia nao deve
        // acontecer: e como empurrar o mouse contra a moldura.
        if (_lastRelative < SpanStart - 0.02 ||
            _lastRelative > SpanStart + SpanLength + 0.02)
        {
            _crossDetector.Reset();
            return;
        }

        GoRemote();
    }

    private bool OnMouse(MouseHookEvent e)
    {
        // Eventos que nos mesmos geramos (SetCursorPos) nao contam.
        if (e.Injected) return false;

        // Em modo local o hook nao decide nada: quem vigia a borda e o
        // movimento bruto, em OnRawMove.
        if (Side == CursorSide.Local) return false;

        // --- daqui para baixo: cursor esta do lado do notebook ---
        try
        {
            switch (e.Message)
            {
                // O movimento em si vai por OnRawMove; aqui so engolimos, para
                // o cursor nao sair do ponto de estacionamento.
                case InputHooks.WM_MOUSEMOVE: break;

                case InputHooks.WM_LBUTTONDOWN: _ = _client.MouseButtonAsync(MouseButtonCode.Left, true); break;
                case InputHooks.WM_LBUTTONUP: _ = _client.MouseButtonAsync(MouseButtonCode.Left, false); break;
                case InputHooks.WM_RBUTTONDOWN: _ = _client.MouseButtonAsync(MouseButtonCode.Right, true); break;
                case InputHooks.WM_RBUTTONUP: _ = _client.MouseButtonAsync(MouseButtonCode.Right, false); break;
                case InputHooks.WM_MBUTTONDOWN: _ = _client.MouseButtonAsync(MouseButtonCode.Middle, true); break;
                case InputHooks.WM_MBUTTONUP: _ = _client.MouseButtonAsync(MouseButtonCode.Middle, false); break;
                case InputHooks.WM_MOUSEWHEEL: _ = _client.MouseWheelAsync(e.WheelDelta); break;
            }
        }
        catch (Exception ex)
        {
            ReturnToLocal($"erro ao enviar o mouse: {ex.Message}");
        }

        return true; // engolido: nao chega em nada aqui no PC
    }

    // ------------------------------------------------------------- teclado

    private bool OnKey(KeyHookEvent e)
    {
        if (e.Injected) return false;

        // Saida de emergencia: funciona mesmo com a rede morta.
        // Ctrl + Alt + Home.
        // O estado de Ctrl e Alt vem do proprio hook, nunca de
        // GetAsyncKeyState.
        //
        // Com o cursor do lado do notebook nos engolimos as teclas para
        // mandar para la - inclusive Ctrl e Alt. Engolida, a tecla nunca e
        // processada pelo Windows daqui, e GetAsyncKeyState continua dizendo
        // que ela esta solta. A saida de emergencia dependia disso e por isso
        // nunca funcionava: justo quando era necessaria, era impossivel de
        // acionar.
        switch (e.VirtualKey)
        {
            case 0x11 or 0xA2 or 0xA3: _ctrlDown = e.Down; break;  // Ctrl / L / R
            case 0x12 or 0xA4 or 0xA5: _altDown = e.Down; break;   // Alt / L / R
        }

        if (e.Down && e.VirtualKey == 0x24 && _ctrlDown && _altDown)  // Home
        {
            if (Side == CursorSide.Remote)
            {
                // ReturnToLocal envia EdgeLeave, e o host solta os
                // modificadores ao receber: sem isso Ctrl e Alt ficariam
                // grudados la, porque o "soltar" acontece aqui.
                _ctrlDown = false;
                _altDown = false;

                ReturnToLocal("atalho de emergencia");
                return true;
            }
        }

        if (Side != CursorSide.Remote) return false;

        try
        {
            _ = _client.KeyAsync(e.VirtualKey, e.Down);
        }
        catch (Exception ex)
        {
            ReturnToLocal($"erro ao enviar o teclado: {ex.Message}");
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------ travessia

    private void GoRemote()
    {
        if (Side == CursorSide.Remote) return;

        try
        {
            if (ShareClipboard) SendClipboard();

            _client.EdgeEnterAsync(_edge, MapToRemote(_lastRelative));

            // Guarda onde o cursor estava. E este ponto que a volta usa: a
            // posicao exata de onde ele saiu, nao uma proporcao recalculada.
            var saida = Cursor.Position;
            _exitX = saida.X;
            _exitY = saida.Y;
            _hasExit = true;

            var bounds = VirtualBounds();

            // Esconde ANTES de mover. Movendo primeiro, o cursor aparece por
            // um instante no ponto de estacionamento - a "piscadinha" longe de
            // onde a pessoa estava olhando.
            if (HideLocalCursor)
            {
                _hider ??= new CursorHider();
                _hider.Show(bounds);
            }

            // Estaciona longe das bordas para haver espaco em todas as
            // direcoes: encostado, os deslocamentos naquele sentido se
            // perderiam no limite da tela.
            //
            // No centro do MONITOR de onde saiu, nunca no centro do desktop
            // virtual: com monitores de alturas diferentes o centro virtual
            // pode cair num vao onde nao existe tela, e o Windows entao joga o
            // cursor para algum canto valido - um pulo que nao vem de gesto
            // nenhum.
            var tela = Screen.FromPoint(saida).Bounds;
            SetCursorPos(
                tela.Left + tela.Width / 2,
                tela.Top + tela.Height / 2);

            Side = CursorSide.Remote;
            SideChanged?.Invoke(Side);
            Status?.Invoke("Cursor foi para o notebook.");
        }
        catch (Exception ex)
        {
            ReturnToLocal($"falha ao atravessar: {ex.Message}");
        }
    }

    private void ReturnToLocal(string motivo)
    {
        if (Side == CursorSide.Local) return;

        // Devolve o cursor ANTES de voltar para o modo local, enquanto o hook
        // ainda engole o movimento. Invertido, o Windows processa o que estiver
        // na fila a partir do ponto de estacionamento e o cursor sai andando de
        // lá antes de ser reposicionado - o que se ve como um salto.
        try
        {
            RestoreCursorToExitPoint();
        }
        catch { }

        Side = CursorSide.Local;

        try { _hider?.Hide(); } catch { }
        try { _ = _client.EdgeLeaveAsync(); } catch { }

        _crossDetector.Reset();

        SideChanged?.Invoke(Side);
        Status?.Invoke($"Controle de volta no PC ({motivo}).");
    }

    /// <summary>
    /// Põe o cursor de volta exatamente onde ele saiu.
    ///
    /// A versao anterior recalculava o ponto por proporcao da altura do
    /// desktop virtual. Isso erra de duas maneiras: a proporcao e tomada sobre
    /// o retangulo que envolve TODOS os monitores, que com telas de alturas
    /// diferentes contem vaos sem tela nenhuma - e cair num vao faz o Windows
    /// realocar o cursor para longe. E, mesmo acertando, devolver "na altura
    /// proporcional" nao e devolver no lugar: basta um pixel de diferenca de
    /// arredondamento para o cursor reaparecer deslocado.
    ///
    /// Como a posicao de saida e conhecida, nao ha o que calcular.
    /// </summary>
    private void RestoreCursorToExitPoint()
    {
        if (_hasExit && PointOnSomeScreen(_exitX, _exitY))
        {
            SetCursorPos(_exitX, _exitY);
            return;
        }

        // Sem ponto de saida confiavel (ou o arranjo de monitores mudou no
        // meio), volta para o centro da tela principal: longe do ideal, mas
        // sempre visivel.
        var principal = Screen.PrimaryScreen?.Bounds ?? Screen.AllScreens[0].Bounds;
        SetCursorPos(
            principal.Left + principal.Width / 2,
            principal.Top + principal.Height / 2);
    }

    /// <summary>
    /// O ponto esta sobre algum monitor de verdade?
    ///
    /// Estar dentro do retangulo que envolve todos nao basta: esse retangulo
    /// tem vaos quando os monitores nao se alinham.
    /// </summary>
    private static bool PointOnSomeScreen(int x, int y)
    {
        var p = new Point(x, y);

        foreach (var s in Screen.AllScreens)
            if (s.Bounds.Contains(p)) return true;

        return false;
    }

    // ----------------------------------------------------------- clipboard

    private void SendClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text.Length < 256 * 1024)
                    _ = _client.SendClipboardAsync(text);
            }
        }
        catch
        {
            // Clipboard ocupado por outro app: nao vale travar a travessia.
        }
    }

    private void ApplyClipboard(string text)
    {
        if (!ShareClipboard || string.IsNullOrEmpty(text)) return;

        try { Clipboard.SetText(text); }
        catch { /* outro app segurando o clipboard */ }
    }

    // --------------------------------------------------------------- utils

    private static string Describe(ScreenEdge e) => e switch
    {
        ScreenEdge.Right => "direita",
        ScreenEdge.Left => "esquerda",
        ScreenEdge.Top => "superior",
        ScreenEdge.Bottom => "inferior",
        _ => e.ToString(),
    };

    private static DesktopBounds VirtualBounds()
    {
        var r = SystemInformation.VirtualScreen;
        return new DesktopBounds(r.Left, r.Top, r.Right, r.Bottom);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _hooks.Dispose();
        _raw?.Dispose();
        _hider?.Dispose();
    }
}

/// <summary>
/// Esconde o cursor cobrindo as telas com uma janela quase invisivel que usa
/// um cursor em branco.
///
/// O Windows nao tem "esconder o cursor globalmente": ShowCursor so vale para
/// a propria thread. Esta e a saida usada por ferramentas do genero.
/// </summary>
internal sealed class CursorHider : Form
{
    public CursorHider()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.01;      // praticamente invisivel, mas recebe o cursor
        Visible = false;

        Cursor = CreateBlankCursor();
    }

    /// <summary>Nao rouba o foco da janela que o usuario estava usando.</summary>
    protected override bool ShowWithoutActivation => true;

    public void Show(DesktopBounds bounds)
    {
        Bounds = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        Visible = true;
        TopMost = true;
    }

    private static Cursor CreateBlankCursor()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Transparent);
            return new Cursor(bmp.GetHicon());
        }
        catch
        {
            return Cursors.Default;
        }
    }
}
