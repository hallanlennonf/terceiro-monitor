using System.Diagnostics;
using System.Drawing.Drawing2D;
using RemoteMirror.Core;

namespace RemoteMirror.Viewer;

/// <summary>
/// A janela que mostra a tela remota.
///
/// Recebe tiles JPEG, compoe numa superficie do tamanho da tela remota e
/// desenha escalado na janela. A entrada do usuario e remapeada para
/// coordenadas da tela remota antes de ir pela rede.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly ViewerClient _client;
    private readonly ViewerSettings _settings;
    private readonly string _hostLabel;

    private readonly Lock _surfaceLock = new();
    private Bitmap? _surface;
    private int _remoteWidth;
    private int _remoteHeight;
    private int _tileSize = Protocol.TileSize;

    private readonly System.Windows.Forms.Timer _repaintTimer = new() { Interval = 15 };
    private volatile bool _dirty;

    private IReadOnlyList<RemoteMonitor> _monitors = [];
    private int _currentMonitor;
    private bool _controlAllowedByHost = true;
    private bool _controlEnabled;
    private string _machineName = "";

    private double _latencyMs;
    private int _framesThisSecond;
    private int _fps;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private long _lastMouseSend;
    private (int X, int Y) _remoteCursor = (-1, -1);

    private FormWindowState _preFullscreenState = FormWindowState.Normal;
    private bool _isFullscreen;
    private bool _closingByUser;

    public ViewerForm(ViewerClient client, ViewerSettings settings, string hostLabel)
    {
        _client = client;
        _settings = settings;
        _hostLabel = hostLabel;
        _controlEnabled = settings.ControlEnabled;

        Text = $"RemoteMirror — {hostLabel}";
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 20, 24);
        KeyPreview = true;
        DoubleBuffered = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable,
            true);

        _client.ServerInfo += OnServerInfo;
        _client.Keyframe += OnKeyframe;
        _client.Tiles += OnTiles;
        _client.Cursor += OnCursor;
        _client.Notice += OnNotice;
        _client.Latency += ms => _latencyMs = ms;
        _client.Disconnected += OnDisconnected;

        _repaintTimer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Invalidate();
            UpdateTitle();
        };
        _repaintTimer.Start();

        if (settings.StartFullscreen) ToggleFullscreen();
    }

    // ------------------------------------------------------------- recepcao

    private void OnServerInfo(
        IReadOnlyList<RemoteMonitor> monitors, int current, bool controlAllowed, string machineName)
    {
        _monitors = monitors;
        _currentMonitor = current;
        _controlAllowedByHost = controlAllowed;
        _machineName = machineName;

        if (!controlAllowed) _controlEnabled = false;

        BeginInvokeSafe(UpdateTitle);
    }

    private void OnKeyframe(KeyframeInfo info, IReadOnlyList<IncomingTile> tiles)
    {
        lock (_surfaceLock)
        {
            if (_surface is null || _remoteWidth != info.Width || _remoteHeight != info.Height)
            {
                _surface?.Dispose();
                _surface = new Bitmap(info.Width, info.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                _remoteWidth = info.Width;
                _remoteHeight = info.Height;
            }

            _tileSize = info.TileSize;
            _currentMonitor = info.MonitorIndex;
            DrawTiles(tiles);
        }

        CountFrame();
        _dirty = true;
    }

    private void OnTiles(IReadOnlyList<IncomingTile> tiles)
    {
        lock (_surfaceLock)
        {
            if (_surface is null) return;
            DrawTiles(tiles);
        }

        CountFrame();
        _dirty = true;
    }

    /// <summary>Decodifica e pinta os tiles na superficie. Chamar com o lock tomado.</summary>
    private void DrawTiles(IReadOnlyList<IncomingTile> tiles)
    {
        if (_surface is null) return;

        using var g = Graphics.FromImage(_surface);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        foreach (var tile in tiles)
        {
            try
            {
                using var ms = new MemoryStream(tile.Jpeg);
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

                // Retangulo explicito em pixels, nunca DrawImageUnscaled:
                // "Unscaled" usa o tamanho FISICO da imagem, que depende do DPI
                // gravado no JPEG e do DPI do bitmap de destino. Quando os dois
                // diferem, cada tile sai esticado e invade o vizinho - a tela
                // chega rasgada em faixas verticais.
                g.DrawImage(
                    img,
                    new Rectangle(
                        tile.Col * _tileSize,
                        tile.Row * _tileSize,
                        img.Width,
                        img.Height));
            }
            catch (ArgumentException)
            {
                // Tile corrompido: ignora. O proximo keyframe corrige.
            }
        }
    }

    private void OnCursor(int x, int y)
    {
        _remoteCursor = (x, y);
        _dirty = true;
    }

    private void OnNotice(string text) => BeginInvokeSafe(() =>
    {
        Text = $"RemoteMirror — {_hostLabel} — {text}";
    });

    private void OnDisconnected(string reason) => BeginInvokeSafe(() =>
    {
        if (_closingByUser) { Close(); return; }

        MessageBox.Show(this,
            $"A conexao caiu.\n\n{reason}",
            "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Close();
    });

    private void CountFrame()
    {
        Interlocked.Increment(ref _framesThisSecond);

        if (_fpsClock.ElapsedMilliseconds >= 1000)
        {
            _fps = Interlocked.Exchange(ref _framesThisSecond, 0);
            _fpsClock.Restart();
        }
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (ObjectDisposedException) { }
    }

    private void UpdateTitle()
    {
        var mode = !_controlAllowedByHost
            ? "so visualizando (host bloqueou)"
            : _controlEnabled ? "controlando" : "so visualizando";

        var monitor = _monitors.Count > 1
            ? $" — monitor {_currentMonitor + 1}/{_monitors.Count}"
            : "";

        Text = $"RemoteMirror — {(_machineName.Length > 0 ? _machineName : _hostLabel)}{monitor}" +
               $" — {mode} — {_latencyMs:F0}ms — {_fps} fps" +
               "   [Ctrl+Alt+F tela cheia · C controle · M monitor · Q sair]";
    }

    // -------------------------------------------------------------- desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        lock (_surfaceLock)
        {
            if (_surface is null)
            {
                DrawWaitingMessage(g);
                return;
            }

            var dest = ComputeDestination();

            g.InterpolationMode = dest.Width >= _remoteWidth
                ? InterpolationMode.NearestNeighbor   // ampliando: sem borrar texto
                : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;

            g.DrawImage(_surface, dest);

            if (!_controlEnabled) DrawRemoteCursor(g, dest);
        }
    }

    private void DrawWaitingMessage(Graphics g)
    {
        var text = "Conectando e recebendo a primeira tela...";
        using var font = new Font("Segoe UI", 12f);
        using var brush = new SolidBrush(Color.FromArgb(150, 160, 175));

        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush,
            (ClientSize.Width - size.Width) / 2,
            (ClientSize.Height - size.Height) / 2);
    }

    /// <summary>
    /// Marca onde esta o mouse do usuario local. So aparece em modo
    /// somente-visualizacao, que e quando o cursor da janela nao e o remoto.
    /// </summary>
    private void DrawRemoteCursor(Graphics g, Rectangle dest)
    {
        if (_remoteCursor.X < 0 || _remoteWidth == 0) return;

        var scale = (float)dest.Width / _remoteWidth;
        var x = dest.X + _remoteCursor.X * scale;
        var y = dest.Y + _remoteCursor.Y * scale;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;

        using var outline = new Pen(Color.FromArgb(200, 0, 0, 0), 3f);
        using var fill = new Pen(Color.FromArgb(230, 255, 210, 60), 1.6f);

        PointF[] arrow =
        [
            new(x, y), new(x, y + 16), new(x + 4.5f, y + 12), new(x + 8, y + 18),
            new(x + 11, y + 16.5f), new(x + 7.5f, y + 10.5f), new(x + 13, y + 10),
        ];

        g.DrawPolygon(outline, arrow);
        g.DrawPolygon(fill, arrow);
    }

    /// <summary>Retangulo onde a tela remota e desenhada, mantendo a proporcao.</summary>
    private Rectangle ComputeDestination()
    {
        if (_remoteWidth == 0 || _remoteHeight == 0) return ClientRectangle;

        var scale = Math.Min(
            (float)ClientSize.Width / _remoteWidth,
            (float)ClientSize.Height / _remoteHeight);

        var w = (int)(_remoteWidth * scale);
        var h = (int)(_remoteHeight * scale);

        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    // --------------------------------------------------------------- mouse

    private bool TryMapToRemote(Point clientPoint, out int rx, out int ry)
    {
        rx = ry = 0;
        if (_remoteWidth == 0) return false;

        var dest = ComputeDestination();
        if (dest.Width == 0 || dest.Height == 0) return false;
        if (!dest.Contains(clientPoint)) return false;

        rx = (int)((clientPoint.X - dest.X) * (float)_remoteWidth / dest.Width);
        ry = (int)((clientPoint.Y - dest.Y) * (float)_remoteHeight / dest.Height);

        rx = Math.Clamp(rx, 0, _remoteWidth - 1);
        ry = Math.Clamp(ry, 0, _remoteHeight - 1);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!CanControl) return;

        // ~120 eventos/s ja e mais do que suficiente e evita inundar o canal.
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastMouseSend).TotalMilliseconds < 8) return;
        _lastMouseSend = now;

        if (TryMapToRemote(e.Location, out var rx, out var ry))
            _ = _client.MouseMoveAsync(rx, ry);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (!CanControl) return;

        if (TryMapToRemote(e.Location, out var rx, out var ry))
            _ = _client.MouseMoveAsync(rx, ry);

        if (TranslateButton(e.Button, out var code))
            _ = _client.MouseButtonAsync(code, down: true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!CanControl) return;

        if (TranslateButton(e.Button, out var code))
            _ = _client.MouseButtonAsync(code, down: false);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (CanControl) _ = _client.MouseWheelAsync(e.Delta);
    }

    private static bool TranslateButton(MouseButtons button, out MouseButtonCode code)
    {
        switch (button)
        {
            case MouseButtons.Left: code = MouseButtonCode.Left; return true;
            case MouseButtons.Right: code = MouseButtonCode.Right; return true;
            case MouseButtons.Middle: code = MouseButtonCode.Middle; return true;
            default: code = default; return false;
        }
    }

    private bool CanControl => _controlEnabled && _controlAllowedByHost && _client.IsConnected;

    // ------------------------------------------------------------- teclado

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    /// <summary>
    /// Intercepta o teclado no nivel da mensagem.
    ///
    /// Sem isto o WinForms come Tab, Alt e as setas antes de chegarem no
    /// handler, e esses sao justamente os que mais importam numa sessao
    /// remota. Os atalhos locais usam Ctrl+Alt e nao sao repassados.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
        {
            var vk = (ushort)(m.WParam.ToInt64() & 0xFFFF);
            var down = m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN;

            if (HandleLocalHotkey(vk, down)) return;

            if (CanControl)
            {
                _ = _client.KeyAsync(vk, down);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private bool HandleLocalHotkey(ushort vk, bool down)
    {
        var combo = ModifierKeys.HasFlag(Keys.Control) && ModifierKeys.HasFlag(Keys.Alt);
        if (!combo) return false;

        // Ctrl e Alt sozinhos continuam indo para o host.
        if (vk is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return false;

        var key = (Keys)vk;
        if (key is not (Keys.F or Keys.C or Keys.M or Keys.Q)) return false;

        if (!down) return true; // consome o key-up do atalho tambem

        switch (key)
        {
            case Keys.F: ToggleFullscreen(); break;
            case Keys.C: ToggleControl(); break;
            case Keys.M: CycleMonitor(); break;
            case Keys.Q: _closingByUser = true; Close(); break;
        }

        return true;
    }

    // --------------------------------------------------------------- acoes

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }
        else
        {
            _preFullscreenState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Maximized
                : FormWindowState.Normal;

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullscreen = true;
        }

        _settings.StartFullscreen = _isFullscreen;
        _settings.Save();
        Invalidate();
    }

    private void ToggleControl()
    {
        if (!_controlAllowedByHost)
        {
            MessageBox.Show(this,
                "O host esta em modo somente visualizacao.\n\n" +
                "Para liberar, marque \"Permitir controle\" no menu da bandeja " +
                "do outro computador.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _controlEnabled = !_controlEnabled;
        _settings.ControlEnabled = _controlEnabled;
        _settings.Save();

        Cursor = _controlEnabled ? Cursors.Cross : Cursors.Default;
        UpdateTitle();
        Invalidate();
    }

    private void CycleMonitor()
    {
        if (_monitors.Count < 2) return;

        _currentMonitor = (_currentMonitor + 1) % _monitors.Count;
        _ = _client.SelectMonitorAsync(_currentMonitor);
        UpdateTitle();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closingByUser = true;
        _repaintTimer.Stop();
        _client.Dispose();

        lock (_surfaceLock)
        {
            _surface?.Dispose();
            _surface = null;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _repaintTimer.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
