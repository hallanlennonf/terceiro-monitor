using RemoteMirror.Core;

namespace RemoteMirror.Viewer;

/// <summary>
/// Ponto de entrada do viewer, chamado pelo executavel unico.
/// </summary>
public static class ViewerEntry
{
    /// <summary>
    /// Abre a tela de conexao e roda a sessao.
    /// Aceita "host [porta]" para atalhos que pulam a tela de conexao,
    /// desde que a senha ja esteja salva nesta maquina.
    /// </summary>
    public static void Run(string[] args)
    {
        var settings = ViewerSettings.Load();

        if (args.Length >= 1)
        {
            var host = args[0];
            var port = args.Length >= 2 && int.TryParse(args[1], out var p) ? p : Protocol.DefaultPort;
            var saved = settings.PasswordFor(host, port);

            if (saved is not null)
            {
                RunSession(host, port, saved, settings);
                return;
            }
        }

        while (true)
        {
            using var connect = new ConnectForm(settings);
            if (connect.ShowDialog() != DialogResult.OK) return;

            var backToList = RunSession(
                connect.SelectedHost, connect.SelectedPort, connect.SelectedPassword, settings);

            if (!backToList) return;
        }
    }

    /// <summary>
    /// Conecta e roda a janela. Devolve true quando faz sentido voltar
    /// para a tela de conexao (senha errada, host fora do ar).
    /// </summary>
    private static bool RunSession(string host, int port, string password, ViewerSettings settings)
    {
        var client = new ViewerClient();

        try
        {
            using var splash = new SplashForm($"Conectando em {host}...");
            splash.Show();
            Application.DoEvents();

            client.ConnectAsync(host, port, password).GetAwaiter().GetResult();
            splash.Close();
        }
        catch (AuthenticationFailedException)
        {
            client.Dispose();
            MessageBox.Show(
                "Senha incorreta.\n\n" +
                "Confira a senha no menu do icone da bandeja do outro computador.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
        catch (Exception ex)
        {
            client.Dispose();
            MessageBox.Show(
                $"Nao consegui conectar em {host}:{port}.\n\n{ex.Message}\n\n" +
                "Confira se o RemoteMirror esta rodando no outro computador e se o " +
                "firewall dele esta liberado (icone da bandeja > Liberar no firewall).",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return true;
        }

        using var form = new ViewerForm(client, settings, host);
        Application.Run(form);
        return false;
    }
}

/// <summary>Janelinha de "conectando", para a tela nao ficar travada sem aviso.</summary>
internal sealed class SplashForm : Form
{
    public SplashForm(string message)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(320, 90);
        BackColor = Color.FromArgb(28, 32, 40);
        ShowInTaskbar = false;

        Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f),
        });
    }
}
