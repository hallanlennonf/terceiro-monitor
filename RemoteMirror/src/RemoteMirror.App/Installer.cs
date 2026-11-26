using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using RemoteMirror.Core;

namespace RemoteMirror.App;

/// <summary>
/// Instalacao embutida no proprio executavel: ele se copia para a pasta do
/// usuario, libera o firewall, se registra na inicializacao e sobe na bandeja.
///
/// Nao existe instalador separado de proposito — um arquivo so e o que torna
/// pratico passar isso para um colega.
/// </summary>
public static class Installer
{
    /// <summary>Pasta onde o host fica instalado.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteMirror");

    /// <summary>Caminho final do executavel instalado.</summary>
    public static string InstalledExe => Path.Combine(InstallDirectory, "RemoteMirror.exe");

    /// <summary>true se ja existe uma copia instalada.</summary>
    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>true se este processo e a copia instalada.</summary>
    public static bool IsRunningFromInstall =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Instala e deixa o host rodando.
    /// </summary>
    /// <param name="error">Preenchido quando algo impede a instalacao.</param>
    /// <returns>true se instalou e iniciou.</returns>
    public static bool Install(out string? error)
    {
        error = null;

        try
        {
            SingleInstance.RequestQuit("host");
            StopMode("--host");
            SingleInstance.WaitForVacancy("host", TimeSpan.FromSeconds(8));

            if (!EnsureInstalledCopy(out error)) return false;

            ConfigureFirewall();

            // Confirma que a copia roda ANTES de registrar no boot: melhor
            // falhar agora, na frente de quem instalou, do que silenciosamente
            // no proximo reinicio.
            if (!LaunchAndConfirm("--host", out error))
            {
                AutoStart.Set(AutoStart.HostValue, InstalledExe, "--host", enabled: false);
                return false;
            }

            // A inicializacao aponta direto para o modo host, sem tela de escolha.
            AutoStart.Set(AutoStart.HostValue, InstalledExe, "--host", enabled: true);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Instala o modo terceiro monitor e o deixa rodando.
    ///
    /// E o lado do PC, nao do notebook: nao abre porta nem aceita conexao, so
    /// vigia a borda da tela. Vale instalar pelo mesmo motivo que o host -
    /// quem usa isso quer o cursor atravessando sempre, nao quer abrir um
    /// programa toda vez que liga o computador.
    /// </summary>
    public static bool InstallShare(out string? error)
    {
        error = null;

        try
        {
            // Pede a vaga em vez de matar: a instancia antiga desfaz os hooks
            // e devolve o cursor antes de sair. E espera de fato: copiar por
            // cima de um executavel ainda em uso falha.
            SingleInstance.RequestQuit("share");
            SingleInstance.WaitForVacancy("share", TimeSpan.FromSeconds(8));

            if (!EnsureInstalledCopy(out error)) return false;

            // Registra no boot DEPOIS de confirmar que a copia roda. Registrar
            // antes foi o que produziu um inicio automatico apontando para uma
            // instalacao quebrada, sem aviso nenhum.
            if (!LaunchAndConfirm("--share", out error))
            {
                AutoStart.Set(AutoStart.ShareValue, InstalledExe, "--share", enabled: false);
                return false;
            }

            AutoStart.Set(AutoStart.ShareValue, InstalledExe, "--share", enabled: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Sobe a copia instalada e confirma que ela continua viva.
    ///
    /// Existe porque uma instalacao pode falhar calada: se o executavel de
    /// origem for o de uma compilacao comum, ele e so um apontador de poucos
    /// kilobytes que depende das DLLs ao lado, e a copia sozinha morre na
    /// hora. Process.Start nao reclama disso - quem reclama e o usuario, no
    /// proximo boot, quando nada sobe.
    /// </summary>
    private static bool LaunchAndConfirm(string arguments, out string? error)
    {
        error = null;

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExe,
                Arguments = arguments,
                UseShellExecute = true,
            });

            if (proc is null)
            {
                error = "Nao consegui iniciar a copia instalada.";
                return false;
            }

            // Uma copia incompleta morre em menos de um segundo.
            if (proc.WaitForExit(2500))
            {
                error =
                    $"A copia instalada encerrou sozinha (codigo {proc.ExitCode}).\n\n" +
                    "Isso acontece quando a instalacao e feita a partir de uma " +
                    "compilacao de desenvolvimento em vez do executavel publicado.";

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Garante que existe a copia em %LOCALAPPDATA%.</summary>
    private static bool EnsureInstalledCopy(out string? error)
    {
        error = null;

        Directory.CreateDirectory(InstallDirectory);

        var source = Environment.ProcessPath;
        if (source is null)
        {
            error = "Nao consegui descobrir o caminho do proprio executavel.";
            return false;
        }

        // A inicializacao do Windows precisa de um caminho estavel: apontar
        // para a pasta de Downloads daria um atalho que para de funcionar na
        // primeira limpeza.
        if (!IsRunningFromInstall) File.Copy(source, InstalledExe, overwrite: true);

        return true;
    }

    /// <summary>Remove tudo que a instalacao criou.</summary>
    public static void Uninstall()
    {
        StopMode("--host");
        StopMode("--share");

        AutoStart.Set(AutoStart.HostValue, InstalledExe, "--host", enabled: false);
        AutoStart.Set(AutoStart.ShareValue, InstalledExe, "--share", enabled: false);

        RemoveFirewallRules();

        try
        {
            if (!IsRunningFromInstall && Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Arquivo em uso: sera limpo na proxima vez.
        }
    }

    /// <summary>
    /// Encerra so as copias rodando NAQUELE modo.
    ///
    /// Matar por nome de processo era um tiro no proprio pe: host e modo
    /// terceiro monitor sao o mesmo executavel, entao instalar o host no PC
    /// derrubava a sessao compartilhada em uso. O modo esta na linha de
    /// comando, e e por ela que a escolha tem que ser feita.
    /// </summary>
    private static void StopMode(string modeArgument)
    {
        var script =
            "Get-CimInstance Win32_Process -Filter \"Name='RemoteMirror.exe'\" | " +
            $"Where-Object {{ $_.CommandLine -like '*{modeArgument}*' -and " +
            $"$_.ProcessId -ne {Environment.ProcessId} }} | " +
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";

        RunPowerShell(script, elevate: false);
    }

    // ------------------------------------------------------------- firewall

    private static string TcpRuleName => $"RemoteMirror (TCP {Protocol.DefaultPort})";
    private static string UdpRuleName => $"RemoteMirror descoberta (UDP {Discovery.Port})";

    /// <summary>
    /// Cria as regras de firewall. Se o processo nao esta elevado, pede
    /// elevacao so para isso — o app em si roda sem admin.
    /// </summary>
    public static void ConfigureFirewall()
    {
        var script = string.Join("; ",
            $"New-NetFirewallRule -DisplayName '{TcpRuleName}' -Direction Inbound -Action Allow " +
            $"-Protocol TCP -LocalPort {Protocol.DefaultPort} -Profile Private,Domain -ErrorAction SilentlyContinue",

            $"New-NetFirewallRule -DisplayName '{UdpRuleName}' -Direction Inbound -Action Allow " +
            $"-Protocol UDP -LocalPort {Discovery.Port} -Profile Private,Domain -ErrorAction SilentlyContinue",

            // Rede marcada como Publica faz o firewall bloquear mesmo com as regras.
            "Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' } | " +
            "Set-NetConnectionProfile -NetworkCategory Private -ErrorAction SilentlyContinue");

        RunPowerShell(script, elevate: !IsElevated);
    }

    private static void RemoveFirewallRules()
    {
        var script = string.Join("; ",
            $"Get-NetFirewallRule -DisplayName '{TcpRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule",
            $"Get-NetFirewallRule -DisplayName '{UdpRuleName}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");

        RunPowerShell(script, elevate: !IsElevated);
    }

    private static void RunPowerShell(string script, bool elevate)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            if (elevate) info.Verb = "runas";

            Process.Start(info)?.WaitForExit(30000);
        }
        catch (Exception)
        {
            // Usuario recusou o UAC: a instalacao segue, e ele pode liberar
            // depois pelo menu da bandeja.
        }
    }
}
