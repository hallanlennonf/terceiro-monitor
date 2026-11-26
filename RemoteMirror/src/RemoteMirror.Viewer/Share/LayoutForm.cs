using RemoteMirror.Core;

namespace RemoteMirror.Viewer.Share;

/// <summary>
/// Onde o notebook fica em relacao aos monitores do PC.
///
/// E a resposta possivel ao "quero ajustar a posicao nas Configuracoes de
/// Video": o Windows so lista monitores de verdade, entao o arranjo mora
/// aqui. Arrastar a tela do notebook define por qual borda o cursor
/// atravessa e em que altura ele reaparece do outro lado.
/// </summary>
public sealed class LayoutForm : Form
{
    private readonly List<(Rectangle Real, Rectangle Draw, string Label)> _monitors = [];

    private readonly Size _resolution;

    private Rectangle _notebookReal;
    private Rectangle _notebookDraw;
    private Rectangle _virtual;

    private float _scale;
    private Point _origin;

    private bool _dragging;
    private Point _grabOffset;

    private readonly Label _hint;

    /// <summary>Borda do PC por onde o cursor atravessa.</summary>
    public ScreenEdge Edge { get; private set; }

    /// <summary>Inicio da faixa que o notebook ocupa, em fracao da altura do PC.</summary>
    public double SpanStart { get; private set; }

    /// <summary>Tamanho da faixa, em fracao da altura do PC.</summary>
    public double SpanLength { get; private set; } = 1.0;

    public LayoutForm(Size notebookResolution, ScreenEdge edge, double spanStart, double spanLength)
    {
        Text = "RemoteMirror - posicao do notebook";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 470);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;

        Edge = edge;
        SpanStart = spanStart;
        SpanLength = spanLength;

        _resolution = notebookResolution.Width > 0 && notebookResolution.Height > 0
            ? notebookResolution
            : new Size(1366, 768);

        _notebookReal = new Rectangle(Point.Empty, _resolution);

        var title = new Label
        {
            Text = "Arraste a tela do notebook para onde ela esta na sua mesa",
            Location = new Point(20, 16),
            Size = new Size(640, 24),
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 40),
        };

        _hint = new Label
        {
            Location = new Point(20, 396),
            Size = new Size(640, 40),
            ForeColor = Color.FromArgb(95, 102, 115),
        };

        var ok = new Button
        {
            Text = "Salvar",
            Location = new Point(536, 428),
            Size = new Size(124, 32),
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(45, 120, 220),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "Cancelar",
            Location = new Point(428, 428),
            Size = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([title, _hint, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        BuildLayout();
        UpdateHint();
    }

    /// <summary>Area util do desenho.</summary>
    private Rectangle Canvas => new(20, 52, 640, 336);

    private void BuildLayout()
    {
        _monitors.Clear();

        var screens = Screen.AllScreens;
        _virtual = screens[0].Bounds;
        foreach (var s in screens) _virtual = Rectangle.Union(_virtual, s.Bounds);

        // Deixa folga em volta para o notebook caber fora dos monitores.
        var totalW = _virtual.Width * 2.2f;
        var totalH = _virtual.Height * 2.2f;

        _scale = Math.Min(Canvas.Width / totalW, Canvas.Height / totalH);

        _origin = new Point(
            Canvas.X + Canvas.Width / 2 - (int)(_virtual.Width * _scale / 2),
            Canvas.Y + Canvas.Height / 2 - (int)(_virtual.Height * _scale / 2));

        var i = 1;
        foreach (var s in screens)
        {
            _monitors.Add((s.Bounds, ToDraw(s.Bounds),
                $"{i++}{(s.Primary ? " (principal)" : "")}\n{s.Bounds.Width}x{s.Bounds.Height}"));
        }

        // O tamanho do retangulo vem da resolucao real da tela do notebook, e
        // so dela.
        //
        // Antes ele era derivado de SpanLength, que por sua vez era calculado
        // a partir do proprio retangulo ao soltar o mouse: cada arraste
        // mudava o tamanho, que mudava a faixa, que mudava o tamanho de novo.
        // Era isso que fazia a telinha pular. Agora a faixa e so resultado.
        var nbW = _resolution.Width;
        var nbH = _resolution.Height;

        var y = _virtual.Top + (int)(_virtual.Height * SpanStart);

        var x = Edge switch
        {
            ScreenEdge.Right => _virtual.Right,
            ScreenEdge.Left => _virtual.Left - nbW,
            _ => _virtual.Left + _virtual.Width / 2 - nbW / 2,
        };

        if (Edge == ScreenEdge.Top) y = _virtual.Top - nbH;
        if (Edge == ScreenEdge.Bottom) y = _virtual.Bottom;

        _notebookReal = new Rectangle(x, y, nbW, nbH);
        _notebookDraw = ToDraw(_notebookReal);

        // Mantem a faixa coerente com o retangulo mesmo que o usuario nunca
        // arraste: o valor salvo pode ter vindo da versao que calculava o
        // tamanho a partir dela.
        AtualizarFaixa(_notebookReal);
    }

    /// <summary>A faixa e sempre consequencia da posicao, nunca o contrario.</summary>
    private void AtualizarFaixa(Rectangle real)
    {
        if (Edge is ScreenEdge.Right or ScreenEdge.Left)
        {
            SpanStart = (real.Y - _virtual.Top) / (double)_virtual.Height;
            SpanLength = real.Height / (double)_virtual.Height;
        }
        else
        {
            SpanStart = (real.X - _virtual.Left) / (double)_virtual.Width;
            SpanLength = real.Width / (double)_virtual.Width;
        }

        SpanStart = Math.Round(SpanStart, 3);
        SpanLength = Math.Max(0.05, Math.Round(SpanLength, 3));
    }

    /// <summary>
    /// Linhas com as quais vale alinhar: as bordas e o centro de cada
    /// monitor. Sem isto, encaixar a telinha com o monitor principal e um
    /// exercicio de pontaria de um pixel.
    /// </summary>
    private List<int> GuiasHorizontais()
    {
        var guias = new List<int>();

        foreach (var (real, _, _) in _monitors)
        {
            guias.Add(real.Top);
            guias.Add(real.Top + real.Height / 2);
            guias.Add(real.Bottom);
        }

        return guias;
    }

    private List<int> GuiasVerticais()
    {
        var guias = new List<int>();

        foreach (var (real, _, _) in _monitors)
        {
            guias.Add(real.Left);
            guias.Add(real.Left + real.Width / 2);
            guias.Add(real.Right);
        }

        return guias;
    }

    /// <summary>
    /// Imanta uma coordenada nas guias, comparando borda inicial, centro e
    /// borda final do retangulo.
    /// </summary>
    private static int Imantar(int inicio, int tamanho, List<int> guias, int tolerancia)
    {
        var melhor = inicio;
        var menorDistancia = tolerancia + 1;

        foreach (var guia in guias)
        {
            // Tres jeitos de encostar: topo, meio e base alinhados com a guia.
            int[] candidatos = [guia, guia - tamanho / 2, guia - tamanho];

            foreach (var candidato in candidatos)
            {
                var distancia = Math.Abs(candidato - inicio);
                if (distancia < menorDistancia)
                {
                    menorDistancia = distancia;
                    melhor = candidato;
                }
            }
        }

        return melhor;
    }

    private Rectangle ToDraw(Rectangle real) => new(
        _origin.X + (int)((real.X - _virtual.X) * _scale),
        _origin.Y + (int)((real.Y - _virtual.Y) * _scale),
        Math.Max(2, (int)(real.Width * _scale)),
        Math.Max(2, (int)(real.Height * _scale)));

    private Rectangle ToReal(Rectangle draw) => new(
        _virtual.X + (int)((draw.X - _origin.X) / _scale),
        _virtual.Y + (int)((draw.Y - _origin.Y) / _scale),
        (int)(draw.Width / _scale),
        (int)(draw.Height / _scale));

    // -------------------------------------------------------------- desenho

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var canvasBrush = new SolidBrush(Color.White);
        using var canvasPen = new Pen(Color.FromArgb(225, 228, 234));
        g.FillRectangle(canvasBrush, Canvas);
        g.DrawRectangle(canvasPen, Canvas);

        using var monitorFill = new SolidBrush(Color.FromArgb(232, 236, 243));
        using var monitorPen = new Pen(Color.FromArgb(150, 160, 178), 1.5f);
        using var labelBrush = new SolidBrush(Color.FromArgb(70, 78, 92));
        using var font = new Font("Segoe UI", 8f);

        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        foreach (var (_, draw, label) in _monitors)
        {
            g.FillRectangle(monitorFill, draw);
            g.DrawRectangle(monitorPen, draw);
            g.DrawString(label, font, labelBrush, draw, format);
        }

        using var nbFill = new SolidBrush(Color.FromArgb(214, 238, 226));
        using var nbPen = new Pen(Color.FromArgb(40, 160, 110), 2f);
        using var nbText = new SolidBrush(Color.FromArgb(22, 95, 65));

        g.FillRectangle(nbFill, _notebookDraw);
        g.DrawRectangle(nbPen, _notebookDraw);
        g.DrawString("notebook", font, nbText, _notebookDraw, format);

        base.OnPaint(e);
    }

    // ------------------------------------------------------------- arrastar

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_notebookDraw.Contains(e.Location))
        {
            _dragging = true;
            _grabOffset = new Point(e.X - _notebookDraw.X, e.Y - _notebookDraw.Y);
            Cursor = Cursors.SizeAll;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _notebookDraw.Location = new Point(e.X - _grabOffset.X, e.Y - _grabOffset.Y);
            Invalidate();
        }
        else
        {
            Cursor = _notebookDraw.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Cursor = Cursors.Default;
            SnapToEdge();
            UpdateHint();
            Invalidate();
        }

        base.OnMouseUp(e);
    }

    /// <summary>
    /// Encosta o notebook na borda mais proxima dos monitores e converte a
    /// posicao em borda + faixa vertical.
    /// </summary>
    private void SnapToEdge()
    {
        // Converte so a POSICAO; o tamanho continua o da tela do notebook,
        // senao o arredondamento ida-e-volta vai encolhendo o retangulo a
        // cada arraste.
        var posicao = ToReal(_notebookDraw);
        var real = new Rectangle(
            posicao.X, posicao.Y, _notebookReal.Width, _notebookReal.Height);

        var monitorsDraw = ToDraw(_virtual);

        // Distancia do centro do notebook a cada lado do conjunto de monitores.
        var cx = _notebookDraw.X + _notebookDraw.Width / 2;
        var cy = _notebookDraw.Y + _notebookDraw.Height / 2;

        var dRight = Math.Abs(cx - monitorsDraw.Right);
        var dLeft = Math.Abs(cx - monitorsDraw.Left);
        var dTop = Math.Abs(cy - monitorsDraw.Top);
        var dBottom = Math.Abs(cy - monitorsDraw.Bottom);

        var menor = Math.Min(Math.Min(dRight, dLeft), Math.Min(dTop, dBottom));

        Edge = menor == dRight ? ScreenEdge.Right
             : menor == dLeft ? ScreenEdge.Left
             : menor == dTop ? ScreenEdge.Top
             : ScreenEdge.Bottom;

        // Reencosta exatamente na borda escolhida e imanta o outro eixo nas
        // guias dos monitores, para dar para alinhar com o monitor principal
        // sem pontaria de um pixel.
        var tolerancia = (int)(28 / Math.Max(_scale, 0.0001f));

        if (Edge is ScreenEdge.Right or ScreenEdge.Left)
        {
            real.X = Edge == ScreenEdge.Right ? _virtual.Right : _virtual.Left - real.Width;
            real.Y = Imantar(real.Y, real.Height, GuiasHorizontais(), tolerancia);
        }
        else
        {
            real.Y = Edge == ScreenEdge.Top ? _virtual.Top - real.Height : _virtual.Bottom;
            real.X = Imantar(real.X, real.Width, GuiasVerticais(), tolerancia);
        }

        AtualizarFaixa(real);

        _notebookReal = real;
        _notebookDraw = ToDraw(real);
    }

    private void UpdateHint()
    {
        var lado = Edge switch
        {
            ScreenEdge.Right => "a direita",
            ScreenEdge.Left => "a esquerda",
            ScreenEdge.Top => "acima",
            ScreenEdge.Bottom => "abaixo",
            _ => "",
        };

        var eixo = Edge is ScreenEdge.Right or ScreenEdge.Left ? "altura" : "largura";

        _hint.Text =
            $"O notebook fica {lado}. O cursor atravessa por essa borda e reaparece " +
            $"na mesma {eixo} proporcional.\n" +
            "Ao soltar, a telinha se encaixa nas bordas e no centro dos monitores. " +
            "Empurrar o mouse fora dessa faixa nao atravessa.";
    }
}
