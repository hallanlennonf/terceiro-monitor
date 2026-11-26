using Microsoft.Win32;

namespace RemoteMirror.Core;

/// <summary>
/// Registro na inicializacao do Windows, por modo.
///
/// Mora no Core porque os dois lados precisam: o host, para estar de pe
/// quando alguem tenta conectar, e o modo terceiro monitor, para o cursor
/// atravessar sem ninguem abrir programa nenhum. Deixar isso no instalador
/// faria o viewer depender dele, e o instalador ja depende do viewer.
///
/// Usa HKEY_CURRENT_USER: nao precisa de administrador e vale so para quem
/// instalou, que e o certo para uma ferramenta pessoal.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Entrada do host: fica aceitando conexao.</summary>
    public const string HostValue = "RemoteMirrorHost";

    /// <summary>Entrada do modo terceiro monitor.</summary>
    public const string ShareValue = "RemoteMirrorShare";

    /// <summary>Liga ou desliga o inicio automatico de um modo.</summary>
    /// <param name="valueName">Use <see cref="HostValue"/> ou <see cref="ShareValue"/>.</param>
    /// <param name="exePath">Caminho do executavel a chamar.</param>
    /// <param name="arguments">Argumentos do modo, por exemplo "--share".</param>
    public static bool Set(string valueName, string exePath, string arguments, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(valueName, $"\"{exePath}\" {arguments}".Trim());
            else key.DeleteValue(valueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>true se o modo esta registrado para iniciar com o Windows.</summary>
    public static bool IsEnabled(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>O que esta registrado hoje, ou null.</summary>
    public static string? CommandFor(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
