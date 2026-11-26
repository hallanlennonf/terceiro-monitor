using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace RemoteMirror.Host;

/// <summary>
/// A interface do host: um icone na bandeja e um menu. De proposito nao tem
/// janela - o host fica fora do caminho de quem esta usando o notebook.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RemoteMirrorHost";

    private readonly HostConfig _config;
    private readonly HostServer _server;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _passwordItem;
    private readonly ToolStripMenuItem _controlItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _monitorsMenu;

    private Icon? _iconIdle;
    private Icon? _iconActive;

    /// <summary>
    /// Janela escondida so para marshalling: a area de transferencia so pode
    /// ser lida e escrita na thread STA da interface, e os eventos do servidor
    /// chegam por threads de rede. Sem um controle para o Invoke, tocar no
    /// clipboard dessas threads lanca excecao.
    /// </summary>
    private readonly Form _marshal = new()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow,
        ShowInTaskbar = false,
        WindowState = FormWindowState.Minimized,
        Opacity = 0,
    };

    public TrayApp()
    {
        _config = HostConfig.Load();
        _server = new HostServer(_config);

        _statusItem = new ToolStripMenuItem("Iniciando...") { Enabled = false };
        _passwordItem = new ToolStripMenuItem("Senha: ...", null, (_, _) => CopyPassword());
        _controlItem = new ToolStripMenuItem("Permitir controle", null, (_, _) => ToggleControl())
        {
            Checked = _config.AllowControl,
            CheckOnClick = true,
        };
        _startupItem = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true,
        };
        _monitorsMenu = new ToolStripMenuItem("Monitor");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_passwordItem);
        menu.Items.Add(new ToolStripMenuItem("Definir senha...", null, (_, _) => SetPassword()));
        menu.Items.Add(new ToolStripMenuItem("Sortear outra senha", null, (_, _) => RegeneratePassword()));
        menu.Items.Add(new ToolStripMenuItem("Mostrar meus IPs", null, (_, _) => ShowAddresses()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_monitorsMenu);
        menu.Items.Add(BuildQualityMenu());
        menu.Items.Add(_controlItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Liberar no firewall", null, (_, _) => OpenFirewall()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Sair", null, (_, _) => ExitApp()));

        menu.Opening += (_, _) => RefreshMenu();

        BuildIcons();

        _icon = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "RemoteMirror - host",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // O WinForms so abre o ContextMenuStrip no botao direito, mas a senha
        // e a primeira coisa que as pessoas procuram - e elas clicam com o
        // esquerdo. Entao abrimos o menu nos dois botoes.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                ShowMenu();
        };

        // Duplo clique vai direto ao que interessa: copiar a senha.
        _icon.DoubleClick += (_, _) => CopyPassword();

        _server.ClientCountChanged += OnClientCountChanged;
        _server.Status += OnStatus;

        // Garante o handle da janela escondida antes de qualquer Invoke.
        _ = _marshal.Handle;

        // PC -> notebook: aplica aqui o que foi copiado no PC (ao atravessar).
        _server.ClipboardReceived += ApplyClipboardFromPc;

        // notebook -> PC: fornece o que foi copiado aqui, lido na thread certa.
        _server.ClipboardProvider = ReadLocalClipboard;

        if (_config.StartWithWindows && !IsStartupEnabled())
            SetStartup(true);

        try
        {
            _server.Start();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Nao consegui abrir a porta {_config.Port}.\n\n{ex.Message}\n\n" +
                "Outro programa pode estar usando essa porta.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
        }
    }

    // ------------------------------------------------------------- eventos

    private void OnClientCountChanged(int count)
    {
        if (_icon.ContextMenuStrip?.InvokeRequired == true)
        {
            _icon.ContextMenuStrip.BeginInvoke(() => OnClientCountChanged(count));
            return;
        }

        _icon.Icon = count > 0 ? _iconActive : _iconIdle;
        UpdateStatus();

        if (count > 0)
        {
            _icon.BalloonTipTitle = "RemoteMirror";
            _icon.BalloonTipText = count == 1
                ? "Um viewer se conectou. Sua tela continua liberada."
                : $"{count} viewers conectados.";
            _icon.ShowBalloonTip(3000);
        }
    }

    private void OnStatus(string message) => Debug.WriteLine($"[host] {message}");

    private void UpdateStatus()
    {
        var count = _server.ClientCount;
        var monitor = _server.CurrentMonitor;

        _statusItem.Text = count == 0
            ? $"Aguardando conexao (porta {_config.Port})"
            : $"{count} viewer(s) conectado(s)";

        _icon.Text = monitor is null
            ? $"RemoteMirror - porta {_config.Port}"
            : $"RemoteMirror - {monitor.Width}x{monitor.Height}, {count} viewer(s)";

        // O Text da bandeja tem limite de 63 caracteres.
        if (_icon.Text.Length > 63) _icon.Text = _icon.Text[..63];
    }

    /// <summary>
    /// Abre o menu na posicao do cursor. Chamar Show() dispara o evento
    /// Opening, entao os dados do menu sao atualizados antes de aparecer.
    /// </summary>
    private void ShowMenu()
    {
        var menu = _icon.ContextMenuStrip;
        if (menu is null) return;

        // Sem isto o menu as vezes fica aberto depois de clicar fora dele.
        menu.Show(Control.MousePosition);
        menu.Focus();
    }

    private void RefreshMenu()
    {
        UpdateStatus();
        _passwordItem.Text = $"Senha: {_config.Password}   (clique para copiar)";
        _controlItem.Checked = _config.AllowControl;
        _startupItem.Checked = IsStartupEnabled();
        RebuildMonitorMenu();
    }

    private void RebuildMonitorMenu()
    {
        _monitorsMenu.DropDownItems.Clear();

        try
        {
            foreach (var m in DesktopDuplicator.ListMonitors())
            {
                var index = m.Index;
                var label = $"{index + 1}. {m.Width}x{m.Height}{(m.IsPrimary ? " (principal)" : "")}";

                var item = new ToolStripMenuItem(label, null, (_, _) =>
                {
                    _config.MonitorIndex = index;
                    _config.Save();
                    _server.SwitchMonitor(index);
                })
                {
                    Checked = index == _config.MonitorIndex,
                };

                _monitorsMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            _monitorsMenu.DropDownItems.Add(new ToolStripMenuItem($"erro: {ex.Message}") { Enabled = false });
        }
    }

    private ToolStripMenuItem BuildQualityMenu()
    {
        var menu = new ToolStripMenuItem("Qualidade");

        (string Label, int Quality, int Fps)[] presets =
        [
            ("Leve - rede fraca",  50, 20),
            ("Equilibrada",        75, 30),
            ("Nitida - LAN boa",   90, 30),
            ("Maxima",             95, 60),
        ];

        foreach (var (label, quality, fps) in presets)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                _config.Quality = quality;
                _config.MaxFps = fps;
                _config.Save();
                _server.RequestKeyframe();
            }));
        }

        return menu;
    }

    // ---------------------------------------------------- area de transferencia

    /// <summary>
    /// Aplica neste computador o texto que foi copiado no PC. Vem de uma thread
    /// de rede; marshalla para a thread STA da interface antes de tocar no
    /// clipboard. Nao bloqueia a rede: dispara e segue.
    /// </summary>
    private void ApplyClipboardFromPc(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        void Aplicar()
        {
            try { Clipboard.SetText(text); } catch { /* outro app segurando */ }
        }

        try
        {
            if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
                _marshal.BeginInvoke(Aplicar);
            else
                Aplicar();
        }
        catch { /* interface encerrando */ }
    }

    /// <summary>
    /// Le o texto da area de transferencia deste computador, sempre na thread
    /// STA. Retorna null se nao houver texto ou se algo der errado - nunca
    /// lanca, porque quem chama e o servidor numa thread de rede.
    /// </summary>
    private string? ReadLocalClipboard()
    {
        string? Ler()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        }

        try
        {
            return _marshal.IsHandleCreated && _marshal.InvokeRequired
                ? (string?)_marshal.Invoke(Ler)
                : Ler();
        }
        catch { return null; }
    }

    // -------------------------------------------------------------- acoes

    private void CopyPassword()
    {
        try
        {
            Clipboard.SetText(_config.Password);
            _icon.BalloonTipTitle = "Senha copiada";
            _icon.BalloonTipText = "Cole no viewer do outro computador.";
            _icon.ShowBalloonTip(2000);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nao consegui copiar: {ex.Message}", "RemoteMirror");
        }
    }

    /// <summary>Senha escolhida a mao, para nao ter que decorar a sorteada.</summary>
    private void SetPassword()
    {
        using var form = new PasswordForm(_config.Password);
        if (form.ShowDialog() != DialogResult.OK) return;

        if (form.Password == _config.Password) return;

        _config.Password = form.Password;
        _config.Save();
        RefreshMenu();

        _icon.BalloonTipTitle = "Senha alterada";
        _icon.BalloonTipText =
            "Quem esta conectado agora continua; as proximas conexoes usam a senha nova.";
        _icon.ShowBalloonTip(4000);
    }

    private void RegeneratePassword()
    {
        var answer = MessageBox.Show(
            "Sortear uma senha nova?\n\nQuem estiver conectado agora continua, " +
            "mas as proximas conexoes vao precisar da senha nova.",
            "RemoteMirror", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes) return;

        _config.Password = HostConfig.GeneratePassword();
        _config.Save();
        CopyPassword();
    }

    private void ToggleControl()
    {
        _config.AllowControl = !_config.AllowControl;
        _config.Save();
        _controlItem.Checked = _config.AllowControl;

        if (!_config.AllowControl) InputInjector.ReleaseAllModifiers();
    }

    private void ShowAddresses()
    {
        var addresses = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Where(a => !a.StartsWith("127."))
            .ToList();

        var text =
            $"Nome desta maquina:\n   {Environment.MachineName}\n\n" +
            $"Enderecos IP:\n   {string.Join("\n   ", addresses)}\n\n" +
            $"Porta: {_config.Port}\n" +
            $"Senha: {_config.Password}\n\n" +
            "Normalmente o viewer acha esta maquina sozinho na lista.\n" +
            "Use o IP so se a descoberta automatica nao funcionar.";

        MessageBox.Show(text, "RemoteMirror - como me achar",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenFirewall()
    {
        var script =
            $"New-NetFirewallRule -DisplayName 'RemoteMirror (TCP {_config.Port})' " +
            $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {_config.Port} " +
            "-Profile Private,Domain -ErrorAction SilentlyContinue; " +
            $"New-NetFirewallRule -DisplayName 'RemoteMirror descoberta (UDP {Core.Discovery.Port})' " +
            $"-Direction Inbound -Action Allow -Protocol UDP -LocalPort {Core.Discovery.Port} " +
            "-Profile Private,Domain -ErrorAction SilentlyContinue";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            MessageBox.Show("Regras de firewall criadas.", "RemoteMirror",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nao consegui criar as regras: {ex.Message}", "RemoteMirror",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------ startup

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nao consegui alterar a inicializacao: {ex.Message}", "RemoteMirror");
        }
    }

    private void ToggleStartup()
    {
        var enabled = !IsStartupEnabled();
        SetStartup(enabled);
        _config.StartWithWindows = enabled;
        _config.Save();
        _startupItem.Checked = IsStartupEnabled();
    }

    // -------------------------------------------------------------- icones

    /// <summary>
    /// Desenha os icones em memoria para o executavel nao depender de
    /// arquivo externo. Cinza parado, verde com viewer conectado.
    /// </summary>
    private void BuildIcons()
    {
        _iconIdle = DrawIcon(Color.FromArgb(120, 130, 140));
        _iconActive = DrawIcon(Color.FromArgb(40, 190, 110));
    }

    private static Icon DrawIcon(Color accent)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(accent);
            using var screen = new SolidBrush(Color.FromArgb(28, 32, 38));

            g.FillRectangle(body, new Rectangle(2, 5, 28, 19));
            g.FillRectangle(screen, new Rectangle(5, 8, 22, 13));
            g.FillRectangle(body, new Rectangle(12, 24, 8, 3));
            g.FillRectangle(body, new Rectangle(8, 27, 16, 3));
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Encerra a partir de outra thread, com seguranca.
    ///
    /// Usado quando uma instalacao nova pede a vaga: o encerramento tem que
    /// passar pela thread da bandeja para o icone sair e a porta ser liberada.
    /// </summary>
    public void RequestExit()
    {
        var strip = _icon.ContextMenuStrip;

        if (strip is not null && strip.InvokeRequired) strip.BeginInvoke(ExitApp);
        else ExitApp();
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        _server.Dispose();
        _icon.Dispose();
        _iconIdle?.Dispose();
        _iconActive?.Dispose();
        _marshal.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

