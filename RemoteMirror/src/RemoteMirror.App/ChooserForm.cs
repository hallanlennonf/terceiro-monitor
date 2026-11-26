namespace RemoteMirror.App;

/// <summary>O que o usuario escolheu fazer nesta maquina.</summary>
public enum LaunchChoice
{
    None,
    InstallHost,
    RunHostOnce,
    RunViewer,
    ShareInput,
    InstallShare,
    Uninstall,
}

/// <summary>
/// Primeira tela do executavel unico: pergunta o papel desta maquina.
///
/// A pergunta e feita em termos do que o usuario quer, nao em jargao —
/// "ser visto" e "ver outro" em vez de "servidor" e "cliente".
/// </summary>
public sealed class ChooserForm : Form
{
    public LaunchChoice Choice { get; private set; } = LaunchChoice.None;

    public ChooserForm()
    {
        Text = "RemoteMirror";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 552);
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "O que este computador vai fazer?",
            Location = new Point(24, 22),
            Size = new Size(432, 28),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 40),
        };

        var subtitle = new Label
        {
            Text = "Instale nos dois computadores e escolha um papel em cada um.",
            Location = new Point(24, 50),
            Size = new Size(432, 20),
            ForeColor = Color.FromArgb(105, 112, 125),
        };

        var hostCard = BuildCard(
            top: 84,
            accent: Color.FromArgb(40, 160, 110),
            heading: "Ser visto por outro computador",
            body: "Instala e fica na bandeja. A tela deste computador\n" +
                  "continua ligada e livre — ninguem e deslogado.",
            buttonText: Installer.IsInstalled ? "Reinstalar" : "Instalar aqui",
            onClick: () => Finish(LaunchChoice.InstallHost));

        var viewerCard = BuildCard(
            top: 212,
            accent: Color.FromArgb(45, 120, 220),
            heading: "Ver a tela de outro computador",
            body: "Abre a lista de computadores da rede.\n" +
                  "Nao instala nada neste aqui.",
            buttonText: "Abrir viewer",
            onClick: () => Finish(LaunchChoice.RunViewer));

        var shareCard = BuildCard(
            top: 340,
            accent: Color.FromArgb(150, 95, 200),
            heading: "Usar o notebook como terceiro monitor",
            body: "O mouse atravessa a borda e entra no notebook,\n" +
                  "que voce olha direto. Fica na bandeja e sobe com o Windows.",
            buttonText: "Instalar e ligar",
            onClick: () => Finish(LaunchChoice.InstallShare));

        var runShareOnce = new LinkLabel
        {
            Text = "Terceiro monitor só agora, sem instalar",
            Location = new Point(24, 456),
            Size = new Size(300, 20),
            LinkColor = Color.FromArgb(105, 112, 125),
        };
        runShareOnce.Click += (_, _) => Finish(LaunchChoice.ShareInput);

        var runOnce = new LinkLabel
        {
            Text = "Só rodar o host agora, sem instalar (para teste)",
            Location = new Point(24, 480),
            Size = new Size(300, 20),
            LinkColor = Color.FromArgb(105, 112, 125),
        };
        runOnce.Click += (_, _) => Finish(LaunchChoice.RunHostOnce);

        Controls.AddRange([title, subtitle, hostCard, viewerCard, shareCard, runShareOnce, runOnce]);

        if (Installer.IsInstalled)
        {
            var uninstall = new LinkLabel
            {
                Text = "Desinstalar",
                Location = new Point(386, 480),
                Size = new Size(80, 20),
                LinkColor = Color.FromArgb(185, 70, 70),
            };
            uninstall.Click += (_, _) => Finish(LaunchChoice.Uninstall);
            Controls.Add(uninstall);
        }
    }

    /// <summary>Monta um dos dois blocos de escolha.</summary>
    private Panel BuildCard(
        int top, Color accent, string heading, string body, string buttonText, Action onClick)
    {
        var panel = new Panel
        {
            Location = new Point(24, top),
            Size = new Size(432, 112),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var stripe = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(4, 110),
            BackColor = accent,
        };

        var headingLabel = new Label
        {
            Text = heading,
            Location = new Point(18, 14),
            Size = new Size(396, 22),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 40),
        };

        var bodyLabel = new Label
        {
            Text = body,
            Location = new Point(18, 38),
            Size = new Size(260, 42),
            ForeColor = Color.FromArgb(95, 102, 115),
        };

        var button = new Button
        {
            Text = buttonText,
            Location = new Point(290, 56),
            Size = new Size(124, 36),
            BackColor = accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => onClick();

        panel.Controls.AddRange([stripe, headingLabel, bodyLabel, button]);
        return panel;
    }

    private void Finish(LaunchChoice choice)
    {
        Choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }
}
