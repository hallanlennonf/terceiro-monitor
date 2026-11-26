using System.ComponentModel;
using RemoteMirror.Core;

namespace RemoteMirror.Viewer;

/// <summary>
/// Tela de entrada: lista os hosts achados na rede e pede a senha.
/// A ideia e que o colega abra, veja o nome da maquina dele na lista,
/// clique e pronto — sem decorar IP.
/// </summary>
public sealed class ConnectForm : Form
{
    private readonly ViewerSettings _settings;

    private readonly ListBox _hostList = new();
    private readonly TextBox _hostBox = new();
    private readonly NumericUpDown _portBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _rememberBox = new();
    private readonly Button _connectButton = new();
    private readonly Button _rescanButton = new();
    private readonly Label _statusLabel = new();

    private List<DiscoveredHost> _found = [];
    private CancellationTokenSource? _scanCts;

    /// <summary>Preenchido quando o usuario confirma.</summary>
    public string SelectedHost { get; private set; } = "";
    public int SelectedPort { get; private set; } = Protocol.DefaultPort;
    public string SelectedPassword { get; private set; } = "";

    public ConnectForm(ViewerSettings settings)
    {
        _settings = settings;

        Text = "RemoteMirror — conectar";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 430);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(250, 250, 252);

        BuildLayout();
        StartScan();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "Computadores encontrados na rede",
            Location = new Point(16, 14),
            Size = new Size(300, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };

        _hostList.Location = new Point(16, 38);
        _hostList.Size = new Size(408, 140);
        _hostList.IntegralHeight = false;
        _hostList.SelectedIndexChanged += OnHostSelected;
        _hostList.DoubleClick += (_, _) => TryConnect();

        _rescanButton.Text = "Procurar de novo";
        _rescanButton.Location = new Point(16, 186);
        _rescanButton.Size = new Size(130, 26);
        _rescanButton.Click += (_, _) => StartScan();

        _statusLabel.Location = new Point(154, 191);
        _statusLabel.Size = new Size(270, 20);
        _statusLabel.ForeColor = Color.FromArgb(100, 105, 115);

        var manualLabel = new Label
        {
            Text = "Ou digite o nome / IP:",
            Location = new Point(16, 228),
            Size = new Size(200, 18),
        };

        _hostBox.Location = new Point(16, 248);
        _hostBox.Size = new Size(300, 24);
        _hostBox.PlaceholderText = "ex.: NOTE-FULANO ou 192.168.0.42";

        var portLabel = new Label
        {
            Text = "Porta:",
            Location = new Point(326, 252),
            Size = new Size(40, 18),
        };

        _portBox.Location = new Point(368, 248);
        _portBox.Size = new Size(56, 24);
        _portBox.Minimum = 1;
        _portBox.Maximum = 65535;
        _portBox.Value = Protocol.DefaultPort;

        var passwordLabel = new Label
        {
            Text = "Senha (aparece na bandeja do outro computador):",
            Location = new Point(16, 284),
            Size = new Size(400, 18),
        };

        _passwordBox.Location = new Point(16, 304);
        _passwordBox.Size = new Size(408, 24);
        _passwordBox.UseSystemPasswordChar = true;
        _passwordBox.Font = new Font("Consolas", 10f);

        var showPassword = new CheckBox
        {
            Text = "Mostrar senha",
            Location = new Point(16, 334),
            Size = new Size(120, 22),
        };
        showPassword.CheckedChanged += (_, _) =>
            _passwordBox.UseSystemPasswordChar = !showPassword.Checked;

        _rememberBox.Text = "Lembrar nesta maquina";
        _rememberBox.Location = new Point(148, 334);
        _rememberBox.Size = new Size(180, 22);
        _rememberBox.Checked = true;

        _connectButton.Text = "Conectar";
        _connectButton.Location = new Point(276, 368);
        _connectButton.Size = new Size(148, 36);
        _connectButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _connectButton.BackColor = Color.FromArgb(45, 120, 220);
        _connectButton.ForeColor = Color.White;
        _connectButton.FlatStyle = FlatStyle.Flat;
        _connectButton.FlatAppearance.BorderSize = 0;
        _connectButton.Click += (_, _) => TryConnect();

        var cancelButton = new Button
        {
            Text = "Cancelar",
            Location = new Point(16, 368),
            Size = new Size(100, 36),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange(
        [
            title, _hostList, _rescanButton, _statusLabel,
            manualLabel, _hostBox, portLabel, _portBox,
            passwordLabel, _passwordBox, showPassword, _rememberBox,
            _connectButton, cancelButton,
        ]);

        AcceptButton = _connectButton;
        CancelButton = cancelButton;
    }

    // ------------------------------------------------------------ descoberta

    private async void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        _rescanButton.Enabled = false;
        _statusLabel.Text = "Procurando na rede...";
        _hostList.Items.Clear();

        try
        {
            // Nao so UDP: numa rede com VPN/Tailscale/Hyper-V o broadcast se
            // perde e o quadradinho ficava vazio. DiscoverAsync tambem varre a
            // sub-rede na porta do servico e acha quem o UDP nao alcanca.
            _found = await HostLocator.DiscoverAsync((int)_portBox.Value, _scanCts.Token);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Falha ao procurar: {ex.Message}";
            _rescanButton.Enabled = true;
            return;
        }

        if (IsDisposed) return;

        foreach (var h in _found)
        {
            var known = _settings.PasswordFor(h.Address.ToString(), h.Port) is not null;
            _hostList.Items.Add($"{h.Name}   —   {h.Address}{(known ? "   (senha salva)" : "")}");
        }

        _statusLabel.Text = _found.Count switch
        {
            0 => "Nenhum host achado na rede. Digite o nome ou o IP abaixo.",
            1 => "1 computador encontrado.",
            _ => $"{_found.Count} computadores encontrados.",
        };

        _rescanButton.Enabled = true;

        if (_found.Count > 0) _hostList.SelectedIndex = 0;
    }

    private void OnHostSelected(object? sender, EventArgs e)
    {
        var i = _hostList.SelectedIndex;
        if (i < 0 || i >= _found.Count) return;

        var host = _found[i];
        _hostBox.Text = host.Address.ToString();
        _portBox.Value = host.Port;

        var saved = _settings.PasswordFor(host.Address.ToString(), host.Port);
        if (saved is not null)
        {
            _passwordBox.Text = saved;
            _connectButton.Focus();
        }
        else
        {
            _passwordBox.Focus();
        }
    }

    // -------------------------------------------------------------- conectar

    private void TryConnect()
    {
        var host = _hostBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("Escolha um computador na lista ou digite o nome/IP.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _hostBox.Focus();
            return;
        }

        if (_passwordBox.Text.Length == 0)
        {
            MessageBox.Show(
                "Falta a senha.\n\nEla aparece no menu do icone do RemoteMirror " +
                "na bandeja do computador que voce quer ver.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _passwordBox.Focus();
            return;
        }

        SelectedHost = host;
        SelectedPort = (int)_portBox.Value;
        SelectedPassword = _passwordBox.Text;

        if (_rememberBox.Checked)
        {
            var label = _hostList.SelectedIndex >= 0 && _hostList.SelectedIndex < _found.Count
                ? _found[_hostList.SelectedIndex].Name
                : null;

            _settings.Remember(SelectedHost, SelectedPort, SelectedPassword, label);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnFormClosing(e);
    }
}
