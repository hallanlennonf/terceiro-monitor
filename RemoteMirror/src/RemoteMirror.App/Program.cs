using RemoteMirror.Host;
using RemoteMirror.Viewer;
using RemoteMirror.Viewer.Share;

namespace RemoteMirror.App;

/// <summary>
/// Executavel unico do RemoteMirror.
///
/// Sem argumentos, pergunta o papel desta maquina. Com argumentos, vai
/// direto, que e como a inicializacao do Windows e os atalhos o chamam:
///
///   RemoteMirror.exe                 tela de escolha
///   RemoteMirror.exe --host          sobe o host na bandeja
///   RemoteMirror.exe --viewer [alvo] abre o viewer
///   RemoteMirror.exe --share         modo terceiro monitor
///   RemoteMirror.exe --install       instala o host e sai
///   RemoteMirror.exe --uninstall     remove a instalacao
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Vale para TODOS os modos, nao so para o host.
        //
        // O modo terceiro monitor nao tinha isto, e uma falha nele significava
        // um processo que simplesmente desaparecia: nenhuma janela, nenhum
        // log, nada para investigar. Um programa que vive na bandeja nao pode
        // sumir calado.
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : null;

        switch (mode)
        {
            case "--host" or "-h" or "/host":
                HostEntry.Run();
                return 0;

            case "--viewer" or "-v" or "/viewer":
                ViewerEntry.Run(args.Skip(1).ToArray());
                return 0;

            case "--share" or "-s" or "/share":
                ShareEntry.Run(args.Skip(1).ToArray());
                return 0;

            case "--install-share" or "/install-share":
                return RunInstallShare() ? 0 : 1;

            case "--install" or "/install":
                return RunInstall(showChooserOnFail: false) ? 0 : 1;

            case "--uninstall" or "/uninstall":
                Installer.Uninstall();
                MessageBox.Show("RemoteMirror removido deste computador.",
                    "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;

            case "--posicao" or "--layout":
                ShareEntry.AdjustLayoutOnly();
                return 0;

            case "--diagborda":
            {
                var saida = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "remotemirror-borda.txt");
                Application.Run(new EdgeDiagnostic(saida));
                return 0;
            }

            case "--help" or "-?" or "/?":
                ShowHelp();
                return 0;
        }

        // Sem argumentos: quem abriu foi uma pessoa, entao pergunte.
        using var chooser = new ChooserForm();
        if (chooser.ShowDialog() != DialogResult.OK) return 0;

        switch (chooser.Choice)
        {
            case LaunchChoice.InstallHost:
                RunInstall(showChooserOnFail: true);
                return 0;

            case LaunchChoice.RunHostOnce:
                HostEntry.Run();
                return 0;

            case LaunchChoice.RunViewer:
                ViewerEntry.Run([]);
                return 0;

            case LaunchChoice.ShareInput:
                ShareEntry.Run([]);
                return 0;

            case LaunchChoice.InstallShare:
                RunInstallShare();
                return 0;

            case LaunchChoice.Uninstall:
                Installer.Uninstall();
                MessageBox.Show("RemoteMirror removido deste computador.",
                    "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
        }

        return 0;
    }

    /// <summary>
    /// Instala o modo terceiro monitor: copia para uma pasta estavel, registra
    /// na inicializacao do Windows e sobe.
    ///
    /// Na primeira vez ele ainda pergunta qual notebook e qual a senha; dali
    /// em diante conecta sozinho, inclusive depois de reiniciar.
    /// </summary>
    /// <summary>
    /// Registra a falha em arquivo e avisa, para nada sumir sem rastro.
    /// </summary>
    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteMirror");

            Directory.CreateDirectory(dir);

            var log = Path.Combine(dir, "crash.log");
            File.AppendAllText(log, $"[{DateTime.Now:O}] modo={Environment.CommandLine}\n{ex}\n\n");

            MessageBox.Show(
                $"O RemoteMirror encontrou um erro:\n\n{ex.Message}\n\nDetalhes em:\n{log}",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Sem nem conseguir logar, nao ha o que fazer.
        }
    }

    private static bool RunInstallShare()
    {
        if (Installer.InstallShare(out var error)) return true;

        MessageBox.Show(
            $"Nao consegui instalar o modo terceiro monitor:\n\n{error}",
            "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);

        return false;
    }

    private static bool RunInstall(bool showChooserOnFail)
    {
        if (!Installer.Install(out var error))
        {
            MessageBox.Show(
                $"Nao consegui instalar:\n\n{error}",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        MessageBox.Show(
            "Instalado e rodando.\n\n" +
            $"Este computador: {Environment.MachineName}\n\n" +
            "Procure o icone do RemoteMirror na bandeja, perto do relogio.\n" +
            "Clique nele e depois em \"Senha\" para copiar a senha — e ela que " +
            "voce usa no outro computador.\n\n" +
            "A tela daqui continua livre: ninguem sera deslogado quando " +
            "alguem conectar.",
            "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);

        return true;
    }

    private static void ShowHelp() => MessageBox.Show(
        "RemoteMirror\n\n" +
        "Sem argumentos            pergunta o que fazer\n" +
        "--host                    sobe o host na bandeja\n" +
        "--viewer [alvo] [porta]   abre o viewer\n" +
        "--share                   modo terceiro monitor\n" +
        "--install                 instala o host\n" +
        "--uninstall               remove a instalacao",
        "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
