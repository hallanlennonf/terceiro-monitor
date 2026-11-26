using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using RemoteMirror.Core;

namespace RemoteMirror.Viewer;

/// <summary>Descricao de um monitor remoto.</summary>
public sealed record RemoteMonitor(int Index, string Name, int Width, int Height, bool IsPrimary);

/// <summary>Um tile recebido, ainda em JPEG.</summary>
public readonly record struct IncomingTile(int Col, int Row, byte[] Jpeg);

/// <summary>Dados do quadro completo que abre uma sessao de video.</summary>
public sealed record KeyframeInfo(int MonitorIndex, int Width, int Height, int TileSize);

/// <summary>
/// Lado cliente: mantem a conexao, traduz as mensagens e expoe eventos.
/// Nao desenha nada — quem desenha e o <see cref="ViewerForm"/>.
/// </summary>
public sealed class ViewerClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private SecureChannel? _channel;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Fila de saida com UM escritor.
    ///
    /// Sem isto, os envios saiam como tasks soltas e podiam chegar fora de
    /// ordem - o que num fluxo de teclado significa um "solta tecla" chegando
    /// antes do "aperta tecla", e a tecla ficando presa na maquina remota.
    /// </summary>
    private readonly Channel<byte[]> _outbox =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Lista de monitores e capacidades, logo apos conectar.</summary>
    public event Action<IReadOnlyList<RemoteMonitor>, int, bool, string>? ServerInfo;

    /// <summary>Quadro completo: recria a superficie.</summary>
    public event Action<KeyframeInfo, IReadOnlyList<IncomingTile>>? Keyframe;

    /// <summary>Quadro parcial: so os tiles que mudaram.</summary>
    public event Action<IReadOnlyList<IncomingTile>>? Tiles;

    /// <summary>Posicao do cursor remoto.</summary>
    public event Action<int, int>? Cursor;

    /// <summary>Mensagem do host (ex.: controle desabilitado).</summary>
    public event Action<string>? Notice;

    /// <summary>Latencia de ida e volta medida pelo ping.</summary>
    public event Action<double>? Latency;

    /// <summary>O cursor saiu do host pela borda: o PC deve retomar o controle.</summary>
    public event Action? EdgeExit;

    /// <summary>Texto copiado no host, para espelhar aqui.</summary>
    public event Action<string>? ClipboardText;

    /// <summary>A conexao caiu. O argumento explica o motivo.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _channel is not null && _tcp.Connected;

    // ------------------------------------------------------------- conexao

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        _tcp.NoDelay = true;

        _channel = await SecureChannel.ConnectAsync(_tcp.GetStream(), password, timeout.Token)
                                      .ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _ = Task.Run(() => SendLoopAsync(_cts.Token), CancellationToken.None);
        _ = Task.Run(() => PingLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "Conexao encerrada.";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _channel!.ReceiveAsync(ct).ConfigureAwait(false);
                Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { reason = "Desconectado."; }
        catch (EndOfStreamException) { reason = "O host fechou a conexao."; }
        catch (Exception ex) { reason = ex.Message; }
        finally
        {
            Disconnected?.Invoke(reason);
        }
    }

    private void Dispatch(byte[] msg)
    {
        if (msg.Length < 1) return;

        using var ms = new MemoryStream(msg);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        switch ((ServerMessage)r.ReadByte())
        {
            case ServerMessage.ServerInfo:
            {
                var count = r.ReadInt32();
                var monitors = new List<RemoteMonitor>(count);

                for (var i = 0; i < count; i++)
                {
                    var name = r.ReadString();
                    var w = r.ReadInt32();
                    var h = r.ReadInt32();
                    var primary = r.ReadBoolean();
                    monitors.Add(new RemoteMonitor(i, name, w, h, primary));
                }

                var current = r.ReadInt32();
                var controlAllowed = r.ReadBoolean();
                var machineName = r.ReadString();

                ServerInfo?.Invoke(monitors, current, controlAllowed, machineName);
                break;
            }

            case ServerMessage.Keyframe:
            {
                var info = new KeyframeInfo(
                    r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                Keyframe?.Invoke(info, ReadTiles(r));
                break;
            }

            case ServerMessage.Tiles:
                Tiles?.Invoke(ReadTiles(r));
                break;

            case ServerMessage.Cursor:
                Cursor?.Invoke(r.ReadInt32(), r.ReadInt32());
                break;

            case ServerMessage.Pong:
            {
                var sent = r.ReadInt64();
                var ms2 = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                Latency?.Invoke(ms2);
                break;
            }

            case ServerMessage.EdgeExit:
                EdgeExit?.Invoke();
                break;

            case ServerMessage.ClipboardText:
                ClipboardText?.Invoke(r.ReadString());
                break;

            case ServerMessage.Notice:
                Notice?.Invoke(r.ReadString());
                break;
        }
    }

    private static List<IncomingTile> ReadTiles(BinaryReader r)
    {
        var count = r.ReadInt32();
        var tiles = new List<IncomingTile>(count);

        for (var i = 0; i < count; i++)
        {
            var col = r.ReadUInt16();
            var row = r.ReadUInt16();
            var len = r.ReadInt32();
            tiles.Add(new IncomingTile(col, row, r.ReadBytes(len)));
        }

        return tiles;
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                await SendAsync(w =>
                {
                    w.Write((byte)ClientMessage.Ping);
                    w.Write(Stopwatch.GetTimestamp());
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* a queda e tratada no loop de recepcao */ }
    }

    // --------------------------------------------------------------- envio

    /// <summary>Drena a fila na ordem, um envio por vez.</summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await _channel!.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { /* a queda e reportada pelo loop de recepcao */ }
    }

    /// <summary>
    /// Enfileira uma mensagem. Sincrono de proposito: e chamado de dentro de
    /// hooks de teclado e mouse, onde nao se pode bloquear nem aguardar.
    /// </summary>
    private Task SendAsync(Action<BinaryWriter> build)
    {
        if (_channel is null) return Task.CompletedTask;

        using var ms = new MemoryStream(32);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            build(w);

        _outbox.Writer.TryWrite(ms.ToArray());
        return Task.CompletedTask;
    }

    public Task RequestKeyframeAsync() =>
        SendAsync(w => w.Write((byte)ClientMessage.RequestKeyframe));

    public Task SelectMonitorAsync(int index) =>
        SendAsync(w => { w.Write((byte)ClientMessage.SelectMonitor); w.Write(index); });

    public Task MouseMoveAsync(int x, int y) =>
        SendAsync(w => { w.Write((byte)ClientMessage.MouseMove); w.Write(x); w.Write(y); });

    public Task MouseButtonAsync(MouseButtonCode button, bool down) =>
        SendAsync(w => { w.Write((byte)ClientMessage.MouseButton); w.Write((byte)button); w.Write(down); });

    public Task MouseWheelAsync(int delta) =>
        SendAsync(w => { w.Write((byte)ClientMessage.MouseWheel); w.Write(delta); });

    public Task KeyAsync(ushort virtualKey, bool down) =>
        SendAsync(w => { w.Write((byte)ClientMessage.KeyEvent); w.Write(virtualKey); w.Write(down); });

    /// <summary>Empurra o cursor remoto por um deslocamento.</summary>
    public Task MouseMoveRelativeAsync(int dx, int dy) =>
        SendAsync(w => { w.Write((byte)ClientMessage.MouseMoveRelative); w.Write(dx); w.Write(dy); });

    /// <summary>
    /// Avisa que o cursor entrou no host por uma borda.
    /// <paramref name="relativePosition"/> vai de 0 (topo) a 1 (base), para o
    /// cursor reaparecer la na mesma altura em que saiu daqui.
    /// </summary>
    public Task EdgeEnterAsync(ScreenEdge edge, double relativePosition) =>
        SendAsync(w =>
        {
            w.Write((byte)ClientMessage.EdgeEnter);
            w.Write((byte)edge);
            w.Write(relativePosition);
        });

    /// <summary>Avisa que o controle voltou para o PC.</summary>
    public Task EdgeLeaveAsync() =>
        SendAsync(w => w.Write((byte)ClientMessage.EdgeLeave));

    /// <summary>Espelha um texto copiado aqui na area de transferencia do host.</summary>
    public Task SendClipboardAsync(string text) =>
        SendAsync(w => { w.Write((byte)ClientMessage.ClipboardText); w.Write(text); });

    /// <summary>Liga ou desliga o envio de video para este cliente.</summary>
    public Task SetVideoEnabledAsync(bool enabled) =>
        SendAsync(w => { w.Write((byte)ClientMessage.SetVideoEnabled); w.Write(enabled); });

    public Task SetQualityAsync(byte quality, byte fps) =>
        SendAsync(w => { w.Write((byte)ClientMessage.SetQuality); w.Write(quality); w.Write(fps); });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { }
        _channel?.Dispose();
        _tcp.Dispose();
        _cts?.Dispose();
    }
}
