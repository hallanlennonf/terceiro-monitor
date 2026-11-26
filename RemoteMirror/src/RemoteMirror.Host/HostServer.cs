using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using RemoteMirror.Core;

namespace RemoteMirror.Host;

/// <summary>
/// O servidor: escuta viewers, captura a tela e distribui os tiles alterados.
///
/// Uma unica thread captura e codifica; cada viewer tem sua propria fila de
/// saida. Assim um viewer lento (wifi ruim) nao segura o quadro dos outros
/// nem a captura.
/// </summary>
public sealed class HostServer : IDisposable
{
    private readonly HostConfig _config;
    private readonly List<ClientSession> _clients = [];
    private readonly Lock _clientsLock = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Discovery.Responder? _responder;
    private Thread? _captureThread;

    private volatile bool _forceKeyframe = true;
    private volatile int _pendingMonitor = -1;
    private bool _disposed;

    /// <summary>Numero de viewers conectados mudou.</summary>
    public event Action<int>? ClientCountChanged;

    /// <summary>Mensagem para a bandeja / log.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Texto copiado no PC. A bandeja aplica na area de transferencia, porque
    /// a API de clipboard exige thread STA.
    /// </summary>
    public event Action<string>? ClipboardReceived;

    /// <summary>Monitor sendo capturado agora.</summary>
    public MonitorInfo? CurrentMonitor { get; private set; }

    public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

    public HostServer(HostConfig config) => _config = config;

    // ------------------------------------------------------------ ciclo de vida

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start();

        _responder = new Discovery.Responder(Environment.MachineName, _config.Port);

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "RemoteMirror.Capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Status?.Invoke($"Escutando na porta {_config.Port}.");
    }

    public void Stop()
    {
        _cts.Cancel();

        try { _listener?.Stop(); } catch { /* ja parado */ }
        _responder?.Dispose();

        lock (_clientsLock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }

        _captureThread?.Join(2000);
    }

    /// <summary>Troca o monitor capturado. Aplicado pela thread de captura.</summary>
    public void SwitchMonitor(int index) => _pendingMonitor = index;

    /// <summary>Forca o proximo quadro a ser completo.</summary>
    public void RequestKeyframe() => _forceKeyframe = true;

    // ---------------------------------------------------------------- accept

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandshakeAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandshakeAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

        try
        {
            tcp.NoDelay = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var stream = tcp.GetStream();
            var channel = await SecureChannel.AcceptAsync(stream, _config.Password, timeout.Token)
                                             .ConfigureAwait(false);

            var session = new ClientSession(tcp, channel, remote);

            lock (_clientsLock) _clients.Add(session);
            ClientCountChanged?.Invoke(ClientCount);
            Status?.Invoke($"Viewer conectado: {remote}");

            await SendServerInfoAsync(session).ConfigureAwait(false);
            _forceKeyframe = true;

            session.SenderTask = Task.Run(() => SenderLoopAsync(session), CancellationToken.None);
            session.ReceiverTask = Task.Run(() => ReceiverLoopAsync(session), CancellationToken.None);
        }
        catch (AuthenticationFailedException)
        {
            Status?.Invoke($"Senha incorreta vinda de {remote}.");
            tcp.Dispose();
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Conexao de {remote} falhou: {ex.Message}");
            tcp.Dispose();
        }
    }

    private void RemoveClient(ClientSession session)
    {
        bool removed;
        lock (_clientsLock) removed = _clients.Remove(session);

        if (!removed) return;

        session.Dispose();
        ClientCountChanged?.Invoke(ClientCount);
        Status?.Invoke($"Viewer saiu: {session.Remote}");

        // Se ninguem mais controla, solta teclas que possam ter ficado presas.
        if (ClientCount == 0) InputInjector.ReleaseAllModifiers();
    }

    // ---------------------------------------------------------------- envio

    private async Task SenderLoopAsync(ClientSession session)
    {
        try
        {
            await foreach (var msg in session.Outbox.Reader.ReadAllAsync(session.Cts.Token)
                                                           .ConfigureAwait(false))
            {
                await session.Channel.SendAsync(msg, session.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status?.Invoke($"Envio para {session.Remote} parou: {ex.Message}");
        }
        finally
        {
            RemoveClient(session);
        }
    }

    private async Task ReceiverLoopAsync(ClientSession session)
    {
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                var msg = await session.Channel.ReceiveAsync(session.Cts.Token).ConfigureAwait(false);
                HandleClientMessage(session, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status?.Invoke($"Conexao com {session.Remote} encerrada: {ex.Message}");
        }
        finally
        {
            RemoveClient(session);
        }
    }

    private void HandleClientMessage(ClientSession session, byte[] msg)
    {
        if (msg.Length < 1) return;

        using var ms = new MemoryStream(msg);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        var type = (ClientMessage)r.ReadByte();

        switch (type)
        {
            case ClientMessage.RequestKeyframe:
                session.NeedsKeyframe = true;
                _forceKeyframe = true;
                break;

            case ClientMessage.SelectMonitor:
                SwitchMonitor(r.ReadInt32());
                break;

            case ClientMessage.Ping:
                var ticks = r.ReadInt64();
                session.TryQueue(BuildPong(ticks));
                break;

            case ClientMessage.SetQuality:
                _config.Quality = Math.Clamp((int)r.ReadByte(), 1, 100);
                _config.MaxFps = Math.Clamp((int)r.ReadByte(), 1, 60);
                break;

            case ClientMessage.EdgeEnter when _config.AllowControl:
            {
                var edge = (ScreenEdge)r.ReadByte();
                var rel = r.ReadDouble();

                session.Edge = new EdgeExitDetector(edge);
                session.SharedActive = true;

                var bounds = InputInjector.GetDesktopBounds();
                var (ex, ey) = session.Edge.EntryPoint(bounds, rel);
                InputInjector.SetCursorPosition(ex, ey);

                session.CursorX = ex;
                session.CursorY = ey;

                Status?.Invoke($"Cursor entrou vindo de {session.Remote}.");
                break;
            }

            case ClientMessage.EdgeLeave:
                session.SharedActive = false;
                session.Edge?.Reset();
                InputInjector.ReleaseAllModifiers();
                Status?.Invoke($"Cursor voltou para {session.Remote}.");
                break;

            case ClientMessage.MouseMoveRelative when _config.AllowControl && session.SharedActive:
            {
                var dx = r.ReadInt32();
                var dy = r.ReadInt32();

                var bounds = InputInjector.GetDesktopBounds();

                // Soma aqui e posiciona de forma absoluta: injetar movimento
                // relativo faria o Windows desta maquina aplicar a aceleracao
                // de ponteiro dela, e o cursor andaria diferente do mouse.
                var cx = Math.Clamp(session.CursorX + dx, bounds.Left, bounds.Right - 1);
                var cy = Math.Clamp(session.CursorY + dy, bounds.Top, bounds.Bottom - 1);

                session.CursorX = cx;
                session.CursorY = cy;

                InputInjector.SetCursorPosition(cx, cy);

                if (session.Edge?.Feed(dx, dy, cx, cy, bounds, Environment.TickCount64) == true)
                {
                    session.SharedActive = false;
                    InputInjector.ReleaseAllModifiers();
                    session.TryQueue(BuildEdgeExit());
                    Status?.Invoke("Cursor saiu pela borda; controle devolvido ao PC.");
                }
                break;
            }

            case ClientMessage.SetVideoEnabled:
                session.WantsVideo = r.ReadBoolean();
                if (session.WantsVideo) _forceKeyframe = true;
                Status?.Invoke(
                    $"{session.Remote}: video {(session.WantsVideo ? "ligado" : "desligado")}.");
                break;

            case ClientMessage.ClipboardText when _config.AllowControl:
                ClipboardReceived?.Invoke(r.ReadString());
                break;

            case ClientMessage.MouseMove when _config.AllowControl:
                InputInjector.MoveMouse(CurrentMonitor!, r.ReadInt32(), r.ReadInt32());
                break;

            case ClientMessage.MouseButton when _config.AllowControl:
                InputInjector.MouseButton((MouseButtonCode)r.ReadByte(), r.ReadBoolean());
                break;

            case ClientMessage.MouseWheel when _config.AllowControl:
                InputInjector.MouseWheel(r.ReadInt32());
                break;

            case ClientMessage.KeyEvent when _config.AllowControl:
                InputInjector.Key(r.ReadUInt16(), r.ReadBoolean());
                break;

            case ClientMessage.MouseMove:
            case ClientMessage.MouseButton:
            case ClientMessage.MouseWheel:
            case ClientMessage.KeyEvent:
                if (!session.WarnedNoControl)
                {
                    session.WarnedNoControl = true;
                    session.TryQueue(BuildNotice("O host esta em modo somente visualizacao."));
                }
                break;
        }
    }

    private async Task SendServerInfoAsync(ClientSession session)
    {
        var monitors = DesktopDuplicator.ListMonitors();

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)ServerMessage.ServerInfo);
            w.Write(monitors.Count);

            foreach (var m in monitors)
            {
                w.Write(m.Name);
                w.Write(m.Width);
                w.Write(m.Height);
                w.Write(m.IsPrimary);
            }

            w.Write(_config.MonitorIndex);
            w.Write(_config.AllowControl);
            w.Write(Environment.MachineName);
        }

        await session.Channel.SendAsync(ms.ToArray(), session.Cts.Token).ConfigureAwait(false);
    }

    private static byte[] BuildPong(long ticks)
    {
        using var ms = new MemoryStream(9);
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMessage.Pong);
        w.Write(ticks);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildEdgeExit()
    {
        using var ms = new MemoryStream(1);
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMessage.EdgeExit);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildNotice(string text)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)ServerMessage.Notice);
            w.Write(text);
        }
        return ms.ToArray();
    }

    private static byte[] BuildFrame(
        bool keyframe, int monitorIndex, int width, int height, List<TileUpdate> tiles)
    {
        using var ms = new MemoryStream(64 * 1024);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)(keyframe ? ServerMessage.Keyframe : ServerMessage.Tiles));

            if (keyframe)
            {
                w.Write(monitorIndex);
                w.Write(width);
                w.Write(height);
                w.Write(Protocol.TileSize);
            }

            w.Write(tiles.Count);
            foreach (var t in tiles)
            {
                w.Write(t.Col);
                w.Write(t.Row);
                w.Write(t.Jpeg.Length);
                w.Write(t.Jpeg);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildCursor(int x, int y)
    {
        using var ms = new MemoryStream(9);
        using var w = new BinaryWriter(ms);
        w.Write((byte)ServerMessage.Cursor);
        w.Write(x);
        w.Write(y);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Entrega so para os clientes que pediram video.</summary>
    private void Broadcast(byte[] message)
    {
        lock (_clientsLock)
        {
            foreach (var c in _clients)
                if (c.WantsVideo) c.TryQueue(message);
        }
    }

    /// <summary>Quantos clientes querem imagem. Zero = nao vale capturar nada.</summary>
    private int VideoClientCount
    {
        get { lock (_clientsLock) return _clients.Count(c => c.WantsVideo); }
    }

    // -------------------------------------------------------------- captura

    private void CaptureLoop()
    {
        DesktopDuplicator? dup = null;
        TileEncoder? encoder = null;
        var monitorIndex = _config.MonitorIndex;

        var lastCursor = (X: int.MinValue, Y: int.MinValue);
        var cursorClock = Stopwatch.StartNew();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Sem ninguem querendo imagem nao ha motivo para capturar nada:
                // no modo "terceiro monitor" isso deixa o host praticamente ocioso.
                if (VideoClientCount == 0)
                {
                    dup?.Dispose(); dup = null;
                    encoder?.Dispose(); encoder = null;
                    _forceKeyframe = true;
                    Thread.Sleep(250);
                    continue;
                }

                var requested = _pendingMonitor;
                if (requested >= 0 && requested != monitorIndex)
                {
                    monitorIndex = requested;
                    _pendingMonitor = -1;
                    _config.MonitorIndex = monitorIndex;
                    dup?.Dispose(); dup = null;
                    encoder?.Dispose(); encoder = null;
                    _forceKeyframe = true;
                }

                if (dup is null)
                {
                    try
                    {
                        dup = new DesktopDuplicator(monitorIndex);
                        encoder = new TileEncoder(dup.Width, dup.Height);
                        CurrentMonitor = dup.Monitor;
                        _forceKeyframe = true;
                        Status?.Invoke($"Capturando {dup.Monitor.Name} ({dup.Width}x{dup.Height}).");
                    }
                    catch (Exception ex)
                    {
                        Status?.Invoke($"Nao consegui iniciar a captura: {ex.Message}");
                        Thread.Sleep(1000);
                        continue;
                    }
                }

                var frameInterval = Math.Max(1, 1000 / Math.Max(1, _config.MaxFps));
                var started = Stopwatch.GetTimestamp();

                try
                {
                    var wantKeyframe = _forceKeyframe;

                    if (dup.TryGetFrame(frameInterval, out var frame))
                    {
                        if (wantKeyframe) encoder!.Invalidate();

                        var tiles = encoder!.Encode(frame, _config.Quality, forceAll: wantKeyframe);

                        if (tiles.Count > 0)
                        {
                            if (wantKeyframe) _forceKeyframe = false;

                            Broadcast(BuildFrame(
                                wantKeyframe, monitorIndex, dup.Width, dup.Height, tiles));
                        }
                    }

                    // Cursor vai separado: muda muito mais que a imagem e
                    // custa 9 bytes, nao um tile JPEG.
                    if (cursorClock.ElapsedMilliseconds >= 40)
                    {
                        cursorClock.Restart();
                        var pos = dup.GetCursorPosition();
                        if (pos != lastCursor)
                        {
                            lastCursor = pos;
                            Broadcast(BuildCursor(pos.X, pos.Y));
                        }
                    }
                }
                catch (DuplicationLostException ex)
                {
                    Status?.Invoke($"Captura reiniciada: {ex.Message}");
                    dup.Dispose(); dup = null;
                    encoder?.Dispose(); encoder = null;
                    _forceKeyframe = true;
                    Thread.Sleep(300);
                    continue;
                }
                catch (Exception ex)
                {
                    Status?.Invoke($"Erro na captura: {ex.Message}");
                    Thread.Sleep(500);
                    continue;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var remaining = frameInterval - (int)elapsed;
                if (remaining > 1) Thread.Sleep(remaining);
            }
        }
        finally
        {
            dup?.Dispose();
            encoder?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _cts.Dispose();
    }

    // -------------------------------------------------------------- sessao

    private sealed class ClientSession(TcpClient tcp, SecureChannel channel, string remote) : IDisposable
    {
        public SecureChannel Channel { get; } = channel;
        public string Remote { get; } = remote;
        public CancellationTokenSource Cts { get; } = new();
        public bool NeedsKeyframe { get; set; } = true;

        /// <summary>
        /// false no modo "terceiro monitor": o cliente so quer input,
        /// e capturar/transmitir a tela seria desperdicio.
        /// </summary>
        public bool WantsVideo { get; set; } = true;

        public bool WarnedNoControl { get; set; }

        /// <summary>Detector de borda do modo "terceiro monitor".</summary>
        public EdgeExitDetector? Edge { get; set; }

        /// <summary>true enquanto o cursor deste PC esta "dentro" do host.</summary>
        public bool SharedActive { get; set; }

        /// <summary>
        /// Posicao que ESTE host mantem para o cursor compartilhado.
        ///
        /// O movimento chega como deslocamento, e seria natural injeta-lo como
        /// movimento relativo - mas o Windows aplica a aceleracao de ponteiro
        /// desta maquina em cima dele, e o resultado nao bate com o movimento
        /// do mouse no outro lado: a sensacao e de sensibilidade diferente da
        /// do PC. Somando aqui e posicionando o cursor de forma absoluta, o
        /// deslocamento vira pixel na razao de um para um.
        /// </summary>
        public int CursorX { get; set; }

        public int CursorY { get; set; }
        public Task? SenderTask { get; set; }
        public Task? ReceiverTask { get; set; }

        /// <summary>
        /// Fila limitada. Se o viewer nao da conta, descartamos o acumulo e
        /// pedimos um keyframe: melhor um salto na imagem do que latencia
        /// crescendo sem parar.
        /// </summary>
        public Channel<byte[]> Outbox { get; } = System.Threading.Channels.Channel
            .CreateBounded<byte[]>(new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });

        public void TryQueue(byte[] message)
        {
            if (!Outbox.Writer.TryWrite(message))
                NeedsKeyframe = true;
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Cts.Cancel(); } catch { }
            Outbox.Writer.TryComplete();
            Channel.Dispose();
            tcp.Dispose();
            Cts.Dispose();
        }
    }
}
