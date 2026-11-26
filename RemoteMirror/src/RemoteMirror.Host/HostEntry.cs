using RemoteMirror.Core;

namespace RemoteMirror.Host;

/// <summary>
/// Ponto de entrada do host, chamado pelo executavel unico.
/// A inicializacao do WinForms fica no executavel; aqui so o que e do host.
/// </summary>
public static class HostEntry
{
    private const string ModeName = "host";

    /// <summary>Sobe o icone na bandeja e bloqueia ate o usuario sair.</summary>
    public static void Run()
    {
        // Duas instancias brigariam pela mesma porta e pelo mesmo icone.
        //
        // A instalacao avisa a instancia antiga para sair, e espera a vaga -
        // em vez de mata-la, que deixaria a porta presa por alguns segundos.
        SingleInstance.RequestQuit(ModeName);

        using var instance = SingleInstance.TryAcquire(ModeName, TimeSpan.FromSeconds(5));

        if (!instance.IsOwner)
        {
            MessageBox.Show(
                "O RemoteMirror ja esta rodando neste computador.\n" +
                "Procure o icone na bandeja, perto do relogio.",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var tray = new TrayApp();
        instance.OnQuitRequested(() => tray.RequestExit());

        Application.Run(tray);
    }

    /// <summary>Grava o erro e avisa, para nao sumir silenciosamente da bandeja.</summary>
    public static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            Directory.CreateDirectory(HostConfig.ConfigDirectory);
            var logPath = Path.Combine(HostConfig.ConfigDirectory, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {ex}\n\n");

            MessageBox.Show(
                $"O host encontrou um erro:\n\n{ex.Message}\n\nDetalhes em:\n{logPath}",
                "RemoteMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Se nem logar deu, nao ha muito a fazer.
        }
    }
}
