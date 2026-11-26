using System.Drawing.Drawing2D;
using RemoteMirror.Core;

namespace RemoteMirror.Viewer.Share;

/// <summary>
/// Ponto de entrada do modo "terceiro monitor".
/// </summary>
public static class ShareEntry
{
    /// <summary>
    /// Abre so a tela de posicionamento e salva o resultado.
    ///
    /// Serve para ajustar o arranjo sem precisar estar conectado - o que
    /// importa aqui e onde o notebook esta na mesa, nao se ele esta ligado.
    /// </summary>
    public static void AdjustLayoutOnly()
    {
        var settings = ViewerSettings.Load();

        // Sem conexao nao sabemos a resolucao do notebook; a da ultima vez
        // nao e guardada, entao vale um tamanho de notebook comum.
        using var form = new LayoutForm(
            new Size(1366, 768),
            settings.ShareEdge,
            settings.ShareSpanStart,
            settings.ShareSpanLength);

        if (form.ShowDialog() != DialogResult.OK) return;

        settings.ShareEdge = form.Edge;
        settings.ShareSpanStart = form.SpanStart;
        settings.ShareSpanLength = form.SpanLength;
        settings.Save();

        MessageBox.Show(
            $"Posicao salva: notebook na borda {EdgeName(form.Edge)}.",
            "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    internal static string EdgeName(ScreenEdge e) => e switch
    {
        ScreenEdge.Right => "direita",
        ScreenEdge.Left => "esquerda",
        ScreenEdge.Top => "de cima",
        ScreenEdge.Bottom => "de baixo",
        _ => e.ToString(),
    };

    /// <summary>Nome do modo para a instancia unica.</summary>
    internal const string ModeName = "share";

    public static void Run(string[] args)
    {
        // Duas instancias deste modo significam dois hooks de mouse brigando
        // pela mesma entrada. Quem chega pede a vaga para quem esta.
        SingleInstance.RequestQuit(ModeName);

        var instance = SingleInstance.TryAcquire(ModeName, TimeSpan.FromSeconds(5));

        if (!instance.IsOwner)
        {
            instance.Dispose();

            MessageBox.Show(
                "O modo terceiro monitor ja esta rodando.\n\n" +
                "Procure o icone de duas telinhas na bandeja, perto do relogio.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var settings = ViewerSettings.Load();

        string host;
        int port;
        string password;

        // Com host e senha guardados, nem pergunta: este modo e para ficar
        // ligado o dia inteiro e subir junto com o Windows, nao para
        // responder dialogo a cada reinicio.
        var saved = settings.ShareHost is null
            ? null
            : settings.PasswordFor(settings.ShareHost, settings.SharePort);

        if (args.Length == 0 && saved is not null)
        {
            host = settings.ShareHost!;
            port = settings.SharePort;
            password = saved;
        }
        else
        {
            using var connect = new ConnectForm(settings);
            if (connect.ShowDialog() != DialogResult.OK) return;

            host = connect.SelectedHost;
            port = connect.SelectedPort;
            password = connect.SelectedPassword;

            settings.ShareHost = host;
            settings.SharePort = port;

            // Guarda a senha mesmo se a pessoa nao marcou "lembrar": sem ela
            // o inicio automatico pararia num dialogo e o modo nao subiria
            // sozinho. Vai cifrada pela conta do Windows, como as outras.
            settings.Remember(host, port, password, null);
            settings.Save();
        }

        ShareLog.Write($"iniciando: host={host}:{port} nome={settings.ShareHostName ?? "(nenhum)"}");

        // A conexao nao e feita aqui: a bandeja sobe primeiro e vai tentando.
        // No boot o notebook costuma estar desligado ou ainda sem rede, e
        // falhar com uma caixa de erro deixaria o modo inutil justamente
        // quando ele deveria ser automatico.
        using (instance)
        {
            var tray = new ShareTrayApp(settings, host, port, password);

            // Sair quando a instalacao pedir: encerrando por aqui a sessao
            // desfaz os hooks e devolve o cursor, em vez de morrer com eles
            // instalados.
            instance.OnQuitRequested(() => tray.RequestExit());

            Application.Run(tray);
        }
    }
}

/// <summary>
/// Bandeja do modo "terceiro monitor": sem janela, so um icone que mostra de
/// que lado o cursor esta, reconecta sozinho e deixa ajustar o arranjo.
/// </summary>
public sealed class ShareTrayApp : ApplicationContext
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);

    private readonly ViewerSettings _settings;
    private int _port;

    /// <summary>Trocavel pelo menu, quando a senha do outro lado muda.</summary>
    private string _password;

    /// <summary>
    /// Endereco atual do host. Nao e readonly porque o notebook troca de IP:
    /// quando a busca encontra outro, e este que passa a valer.
    /// </summary>
    private string _host;

    /// <summary>
    /// Janela invisivel que serve de ancora para voltar a thread de UI.
    ///
    /// A conexao e tentada em segundo plano, e tudo que mexe no icone ou nos
    /// hooks precisa rodar na thread que criou a bomba de mensagens.
    /// </summary>
    private readonly Form _anchor;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _reconnectItem;
    private readonly ToolStripMenuItem _clipboardItem;
    private readonly ToolStripMenuItem _hideCursorItem;
    private readonly ToolStripMenuItem _startupItem;

    private CancellationTokenSource _cts = new();
    private ViewerClient? _client;
    private ShareSession? _session;

    private Icon? _iconOffline;
    private Icon? _iconLocal;
    private Icon? _iconRemote;

    private Size _remoteResolution = new(1366, 768);
    private bool _connected;
    private bool _warnedBadPassword;
    private string? _lastError;
    private bool _exiting;

    public ShareTrayApp(ViewerSettings settings, string host, int port, string password)
    {
        _settings = settings;
        _host = host;
        _port = port;
        _password = password;

        _anchor = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            Opacity = 0,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
        };

        // Forca a criacao do handle: sem ele nao da para marshalar nada.
        _ = _anchor.Handle;

        _statusItem = new ToolStripMenuItem("Conectando...") { Enabled = false };
        _reconnectItem = new ToolStripMenuItem("Tentar conectar agora", null, (_, _) => Reconnect());

        _clipboardItem = new ToolStripMenuItem(
            "Compartilhar area de transferencia", null, (_, _) => ToggleClipboard())
        { Checked = settings.ShareClipboard, CheckOnClick = true };

        _hideCursorItem = new ToolStripMenuItem(
            "Esconder o cursor ao atravessar", null, (_, _) => ToggleHideCursor())
        { Checked = settings.ShareHideCursor, CheckOnClick = true };

        _startupItem = new ToolStripMenuItem(
            "Iniciar com o Windows", null, (_, _) => ToggleStartup())
        { Checked = AutoStart.IsEnabled(AutoStart.ShareValue), CheckOnClick = true };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_reconnectItem);
        menu.Items.Add(new ToolStripMenuItem("Informar outra senha...", null, (_, _) => ChangeCredentials()));
        menu.Items.Add(new ToolStripMenuItem("Ajustar posicao do notebook...", null, (_, _) => AdjustLayout()));
        menu.Items.Add(new ToolStripMenuItem("Trazer o cursor de volta", null, (_, _) => ForceReturn()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildSensitivityMenu());
        menu.Items.Add(_clipboardItem);
        menu.Items.Add(_hideCursorItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Sair", null, (_, _) => ExitApp()));

        BuildIcons();

        _icon = new NotifyIcon
        {
            Icon = _iconOffline,
            Text = "RemoteMirror - conectando",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // O balao de senha recusada e a unica coisa que a pessoa ve quando a
        // senha muda do outro lado; clicar nele precisa levar a algum lugar.
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_warnedBadPassword) ChangeCredentials();
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                menu.Show(Control.MousePosition);
        };

        UpdateStatus();

        _ = ConnectLoopAsync(_cts.Token);
    }

    // ----------------------------------------------------------- conexao

    /// <summary>
    /// Tenta conectar ate conseguir, com espera crescente.
    ///
    /// Senha errada e a excecao: isso nao se resolve esperando, entao o laco
    /// para e avisa. Qualquer outra falha - notebook desligado, rede ainda
    /// subindo, cabo solto - e temporaria por natureza.
    /// </summary>
    private async Task ConnectLoopAsync(CancellationToken token)
    {
        var delay = FirstRetry;

        while (!token.IsCancellationRequested)
        {
            // O endereco guardado pode ter envelhecido: o roteador da outro IP
            // ao notebook e nada mais funciona. A busca devolve candidatos, e
            // quem confirma a identidade e o handshake - so passa com a senha.
            List<string> candidatos;

            try
            {
                candidatos = await HostLocator.CandidatesAsync(
                    _host, _settings.ShareHostName, _port, token);
            }
            catch (OperationCanceledException) { return; }
            catch { candidatos = [_host]; }

            if (candidatos.Count == 0) candidatos = [_host];

            ShareLog.Write($"candidatos ({candidatos.Count}): {string.Join(", ", candidatos)}");

            var senhaRecusada = false;
            Exception? ultimaFalha = null;

            foreach (var endereco in candidatos)
            {
                if (token.IsCancellationRequested) return;

                var client = new ViewerClient();

                try
                {
                    await client.ConnectAsync(endereco, _port, _password).WaitAsync(token);

                    ShareLog.Write($"conectado em {endereco}:{_port}");

                    if (!string.Equals(endereco, _host, StringComparison.OrdinalIgnoreCase))
                        OnUi(() => RememberNewAddress(endereco));

                    OnUi(() => OnConnected(client));
                    return;
                }
                catch (OperationCanceledException)
                {
                    client.Dispose();
                    return;
                }
                catch (AuthenticationFailedException)
                {
                    // Achou um host, mas a senha nao serve. Pode ser outro
                    // computador qualquer rodando o programa, entao vale
                    // continuar a lista antes de desistir.
                    client.Dispose();
                    senhaRecusada = true;
                    ShareLog.Write($"{endereco}: senha recusada");
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    ultimaFalha = ex;
                    ShareLog.Write($"{endereco}: {ex.GetType().Name} - {ex.Message}");
                }
            }

            // Senha recusada tem precedencia sobre qualquer falha de rede.
            //
            // Antes eu so avisava quando NENHUM outro candidato tinha falhado
            // por outro motivo - e como a lista sempre contem o endereco
            // antigo, que da erro de rede, a recusa de senha ficava encoberta.
            // O resultado era o pior possivel: tentar para sempre, calado,
            // por um motivo que so a pessoa pode resolver.
            if (senhaRecusada)
            {
                OnUi(() =>
                {
                    _lastError = "a senha salva foi recusada";
                    UpdateStatus();

                    if (!_warnedBadPassword)
                    {
                        _warnedBadPassword = true;

                        // Aviso na bandeja, nao caixa de mensagem: a caixa trava
                        // a thread da interface - e com ela o laco de conexao -
                        // e, sem janela dona, o Windows a deixa atras das
                        // outras. Quem reportou "nao conecta mais" nunca a viu.
                        // O balao aparece sobre tudo, nao bloqueia nada, e o
                        // clique leva direto para onde se resolve.
                        _icon.BalloonTipTitle = "A senha do notebook nao e mais a mesma";
                        _icon.BalloonTipText =
                            "Achei o computador, mas a senha salva foi recusada. " +
                            "Clique aqui para informar a senha nova.";
                        _icon.ShowBalloonTip(10000);
                    }
                });
            }
            else
            {
                OnUi(() =>
                {
                    _lastError = ultimaFalha?.Message ?? "nao encontrei o host na rede";
                    UpdateStatus();
                });
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) { return; }

            // Espera crescente: no boot vale insistir rapido, mas depois de
            // minutos sem o notebook nao faz sentido bater na porta a cada
            // tres segundos.
            ShareLog.Write($"nova tentativa em {delay.TotalSeconds:0}s");

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.6, MaxRetry.TotalSeconds));
        }
    }

    /// <summary>
    /// Passa a usar o endereco novo e o guarda, junto com a senha.
    ///
    /// Sem gravar, a proxima vez comecaria de novo pelo endereco velho e
    /// pagaria a busca inteira outra vez.
    /// </summary>
    private void RememberNewAddress(string endereco)
    {
        var anterior = _host;
        _host = endereco;

        _settings.ShareHost = endereco;
        _settings.SharePort = _port;
        _settings.Remember(endereco, _port, _password, _settings.ShareHostName);
        _settings.Save();

        _icon.BalloonTipTitle = "O notebook mudou de endereco";
        _icon.BalloonTipText = $"Era {anterior} e agora e {endereco}. Guardado.";
        _icon.ShowBalloonTip(4000);
    }

    private void OnConnected(ViewerClient client)
    {
        if (_exiting)
        {
            client.Dispose();
            return;
        }

        _client = client;
        _connected = true;
        _lastError = null;

        client.ServerInfo += OnServerInfo;
        client.Disconnected += OnDisconnected;

        _session = new ShareSession(client, _settings.ShareEdge)
        {
            ShareClipboard = _settings.ShareClipboard,
            HideLocalCursor = _settings.ShareHideCursor,
            SpanStart = _settings.ShareSpanStart,
            SpanLength = _settings.ShareSpanLength,
            Sensitivity = _settings.ShareSensitivity,
        };

        _session.SideChanged += side => OnUi(() =>
        {
            _icon.Icon = side == CursorSide.Remote ? _iconRemote : _iconLocal;
            UpdateStatus();
        });

        try
        {
            _session.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Conectei, mas nao consegui ligar o compartilhamento:\n\n{ex.Message}",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);

            ExitApp();
            return;
        }

        UpdateStatus();

        _icon.BalloonTipTitle = "Terceiro monitor ligado";
        _icon.BalloonTipText =
            $"Empurre o mouse para a borda {ShareEntry.EdgeName(_settings.ShareEdge)} para entrar no " +
            $"{_host}. Ctrl+Alt+Home traz o cursor de volta a qualquer momento.";
        _icon.ShowBalloonTip(5000);
    }

    private void OnDisconnected(string? motivo) => OnUi(() =>
    {
        if (_exiting) return;

        _connected = false;
        _lastError = motivo;

        // A sessao solta o cursor sozinha ao cair; aqui so desmontamos.
        _session?.Dispose();
        _session = null;

        _client?.Dispose();
        _client = null;

        UpdateStatus();

        // Volta a tentar: o notebook pode ter so reiniciado.
        _ = ConnectLoopAsync(_cts.Token);
    });

    /// <summary>
    /// Troca host e senha sem fechar o programa.
    ///
    /// Antes a orientacao era "saia e abra de novo", o que e pedir para a
    /// pessoa desfazer o que acabou de instalar so porque uma senha mudou.
    /// </summary>
    private void ChangeCredentials()
    {
        using var connect = new ConnectForm(_settings);
        if (connect.ShowDialog() != DialogResult.OK) return;

        // Desmonta a conexao atual, se houver: os dados mudaram.
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _session?.Dispose();
        _session = null;

        _client?.Dispose();
        _client = null;

        _connected = false;
        _warnedBadPassword = false;
        _lastError = null;

        _host = connect.SelectedHost;
        _port = connect.SelectedPort;
        _password = connect.SelectedPassword;

        _settings.ShareHost = _host;
        _settings.SharePort = _port;
        _settings.Remember(_host, _port, _password, _settings.ShareHostName);
        _settings.Save();

        UpdateStatus();

        _ = ConnectLoopAsync(_cts.Token);
    }

    private void Reconnect()
    {
        if (_connected) return;

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _lastError = null;
        UpdateStatus();

        _ = ConnectLoopAsync(_cts.Token);
    }

    // ------------------------------------------------------------- eventos

    private void OnServerInfo(
        IReadOnlyList<RemoteMonitor> monitors, int current, bool controlAllowed, string machineName)
    {
        if (monitors.Count > 0)
        {
            var m = monitors[Math.Clamp(current, 0, monitors.Count - 1)];
            _remoteResolution = new Size(m.Width, m.Height);
        }

        // Guarda o nome na primeira conexao: e por ele que a busca reconhece
        // o notebook quando o IP mudar.
        if (!string.IsNullOrWhiteSpace(machineName) &&
            !string.Equals(_settings.ShareHostName, machineName, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ShareHostName = machineName;
            _settings.Save();
        }

        if (!controlAllowed)
        {
            OnUi(() => MessageBox.Show(
                "O notebook esta em modo somente visualizacao.\n\n" +
                "Marque \"Permitir controle\" no menu da bandeja dele, " +
                "senao o cursor nao vai responder la.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
    }

    private void UpdateStatus()
    {
        string texto;

        if (!_connected)
        {
            texto = _lastError is null
                ? $"Procurando {_host}..."
                : $"Sem conexao com {_host}";
        }
        else
        {
            texto = _session?.Side == CursorSide.Remote
                ? $"Cursor no {_host}"
                : "Cursor aqui no PC";
        }

        _statusItem.Text = texto;
        _reconnectItem.Enabled = !_connected;

        _icon.Icon = !_connected ? _iconOffline
                   : _session?.Side == CursorSide.Remote ? _iconRemote
                   : _iconLocal;

        var tip = $"RemoteMirror - {texto}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>Roda na thread de UI, venha de onde vier.</summary>
    private void OnUi(Action action)
    {
        if (_anchor.IsDisposed) return;

        try
        {
            if (_anchor.InvokeRequired) _anchor.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* fechando */ }
    }

    // --------------------------------------------------------------- acoes

    private void AdjustLayout()
    {
        using var form = new LayoutForm(
            _remoteResolution, _settings.ShareEdge, _settings.ShareSpanStart, _settings.ShareSpanLength);

        if (form.ShowDialog() != DialogResult.OK) return;

        _settings.ShareEdge = form.Edge;
        _settings.ShareSpanStart = form.SpanStart;
        _settings.ShareSpanLength = form.SpanLength;
        _settings.Save();

        if (_session is not null)
        {
            _session.Edge = form.Edge;
            _session.SpanStart = form.SpanStart;
            _session.SpanLength = form.SpanLength;
        }

        _icon.BalloonTipTitle = "Posicao salva";
        _icon.BalloonTipText =
            $"O notebook esta na borda {ShareEntry.EdgeName(form.Edge)}. " +
            "Empurre o mouse para la para atravessar.";
        _icon.ShowBalloonTip(3000);
    }

    private void ForceReturn()
    {
        if (_session?.Side != CursorSide.Remote) return;

        _session.Stop();
        _session.Start();
    }

    /// <summary>
    /// Sensibilidade do mouse do lado do notebook.
    ///
    /// Precisa ser ajustavel porque o movimento vai cru, sem a curva de
    /// aceleracao do Windows daqui: quanto disso incomoda depende do mouse e
    /// da configuracao de cada um.
    /// </summary>
    private ToolStripMenuItem BuildSensitivityMenu()
    {
        var raiz = new ToolStripMenuItem("Sensibilidade do mouse la");

        (string Nome, double Valor)[] opcoes =
        [
            ("Bem leve (0,5x)", 0.5),
            ("Leve (0,75x)", 0.75),
            ("Igual ao mouse (1x)", 1.0),
            ("Mais rapida (1,5x)", 1.5),
            ("Bem rapida (2x)", 2.0),
        ];

        foreach (var (nome, valor) in opcoes)
        {
            var item = new ToolStripMenuItem(nome)
            {
                Checked = Math.Abs(_settings.ShareSensitivity - valor) < 0.001,
            };

            item.Click += (_, _) =>
            {
                _settings.ShareSensitivity = valor;
                _settings.Save();

                if (_session is not null) _session.Sensitivity = valor;

                foreach (ToolStripMenuItem outro in raiz.DropDownItems)
                    outro.Checked = ReferenceEquals(outro, item);
            };

            raiz.DropDownItems.Add(item);
        }

        return raiz;
    }

    private void ToggleClipboard()
    {
        _settings.ShareClipboard = !_settings.ShareClipboard;
        _settings.Save();

        if (_session is not null) _session.ShareClipboard = _settings.ShareClipboard;
        _clipboardItem.Checked = _settings.ShareClipboard;
    }

    private void ToggleHideCursor()
    {
        _settings.ShareHideCursor = !_settings.ShareHideCursor;
        _settings.Save();

        if (_session is not null) _session.HideLocalCursor = _settings.ShareHideCursor;
        _hideCursorItem.Checked = _settings.ShareHideCursor;
    }

    private void ToggleStartup()
    {
        var ligar = !AutoStart.IsEnabled(AutoStart.ShareValue);
        var exe = Environment.ProcessPath;

        if (exe is null)
        {
            _startupItem.Checked = false;
            return;
        }

        var ok = AutoStart.Set(AutoStart.ShareValue, exe, "--share", ligar);
        _startupItem.Checked = ok && ligar;

        if (!ok)
        {
            MessageBox.Show(
                "Nao consegui alterar o inicio automatico.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // -------------------------------------------------------------- icones

    private void BuildIcons()
    {
        _iconOffline = DrawIcon(Color.FromArgb(150, 90, 90), false, dimmed: true);
        _iconLocal = DrawIcon(Color.FromArgb(120, 130, 140), false, dimmed: false);
        _iconRemote = DrawIcon(Color.FromArgb(40, 160, 110), true, dimmed: false);
    }

    /// <summary>
    /// Duas telas lado a lado; a da direita acesa quando o cursor esta la, as
    /// duas apagadas quando nao ha conexao.
    /// </summary>
    private static Icon DrawIcon(Color accent, bool remoteActive, bool dimmed)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var dim = new SolidBrush(Color.FromArgb(dimmed ? 60 : 90, 98, 110));
            using var hot = new SolidBrush(accent);

            var esquerda = new Rectangle(2, 8, 13, 16);
            var direita = new Rectangle(17, 10, 13, 14);

            if (dimmed)
            {
                g.FillRectangle(dim, esquerda);
                g.FillRectangle(dim, direita);
            }
            else
            {
                g.FillRectangle(remoteActive ? dim : hot, esquerda);
                g.FillRectangle(remoteActive ? hot : dim, direita);
            }
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Encerra de fora da thread de UI, com seguranca.
    ///
    /// Usado quando outra instancia pede a vaga: precisa passar pela thread
    /// que instalou os hooks, senao eles ficam para tras e o cursor some.
    /// </summary>
    public void RequestExit() => OnUi(ExitApp);

    private void ExitApp()
    {
        _exiting = true;

        try { _cts.Cancel(); } catch { }

        _icon.Visible = false;

        _session?.Dispose();
        _client?.Dispose();
        _icon.Dispose();

        _iconOffline?.Dispose();
        _iconLocal?.Dispose();
        _iconRemote?.Dispose();

        _anchor.Dispose();

        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;

            try { _cts.Cancel(); _cts.Dispose(); } catch { }

            _session?.Dispose();
            _client?.Dispose();
            _icon.Dispose();
            _anchor.Dispose();
        }

        base.Dispose(disposing);
    }
}
