namespace RemoteMirror.Host;

/// <summary>
/// Deixa a pessoa escolher a propria senha em vez de decorar a sorteada.
///
/// Tem um minimo porque esta senha e a unica coisa entre a rede e o controle
/// total deste computador - mouse, teclado e tela. Numa rede de empresa isso
/// nao e detalhe. Abaixo do confortavel o aviso aparece, mas a escolha fica
/// com quem usa.
/// </summary>
public sealed class PasswordForm : Form
{
    private const int MinimumLength = 6;
    private const int ComfortableLength = 10;

    private readonly TextBox _box;
    private readonly Label _warning;

    /// <summary>A senha escolhida, valida apenas com DialogResult.OK.</summary>
    public string Password => _box.Text;

    public PasswordForm(string current)
    {
        Text = "RemoteMirror - definir senha";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 230);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "Escolha a senha deste computador",
            Location = new Point(20, 18),
            Size = new Size(400, 24),
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 40),
        };

        var hint = new Label
        {
            Text = "E ela que voce informa no outro computador para conectar.",
            Location = new Point(20, 44),
            Size = new Size(400, 20),
            ForeColor = Color.FromArgb(105, 112, 125),
        };

        _box = new TextBox
        {
            Text = current,
            Location = new Point(20, 76),
            Size = new Size(400, 28),
            Font = new Font("Consolas", 12f),
            UseSystemPasswordChar = false,
        };
        _box.TextChanged += (_, _) => UpdateWarning();

        var show = new Label
        {
            Text = "A senha fica visivel: ninguem digita de cor o que nao leu.",
            Location = new Point(20, 108),
            Size = new Size(400, 18),
            ForeColor = Color.FromArgb(140, 146, 158),
        };

        _warning = new Label
        {
            Location = new Point(20, 132),
            Size = new Size(400, 36),
            ForeColor = Color.FromArgb(185, 120, 40),
        };

        var ok = new Button
        {
            Text = "Salvar",
            Location = new Point(296, 182),
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
            Location = new Point(188, 182),
            Size = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
        };

        var generate = new Button
        {
            Text = "Sortear",
            Location = new Point(20, 182),
            Size = new Size(100, 32),
        };
        generate.Click += (_, _) =>
        {
            _box.Text = HostConfig.GeneratePassword();
            UpdateWarning();
        };

        Controls.AddRange([title, hint, _box, show, _warning, generate, cancel, ok]);

        AcceptButton = ok;
        CancelButton = cancel;

        // Nao deixa fechar com OK numa senha curta demais.
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;

            if (_box.Text.Length < MinimumLength)
            {
                MessageBox.Show(
                    $"A senha precisa de pelo menos {MinimumLength} caracteres.",
                    "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                e.Cancel = true;
            }
        };

        UpdateWarning();
    }

    private void UpdateWarning()
    {
        var len = _box.Text.Length;

        _warning.Text = len switch
        {
            < MinimumLength =>
                $"Curta demais: faltam {MinimumLength - len} caractere(s).",

            < ComfortableLength =>
                "Funciona, mas e curta. Esta senha da controle do mouse, do teclado " +
                "e da tela deste computador a quem estiver na mesma rede.",

            _ => "",
        };
    }
}
