using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteMirror.Viewer;

/// <summary>Uma conexao que o usuario ja usou antes.</summary>
public sealed class SavedHost
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = Core.Protocol.DefaultPort;
    public string? Label { get; set; }

    /// <summary>Senha protegida por DPAPI, em base64.</summary>
    public string? ProtectedPassword { get; set; }
}

/// <summary>
/// Preferencias do viewer, em %APPDATA%\RemoteMirror\viewer.json.
/// Senhas gravadas vao cifradas com DPAPI da conta atual.
/// </summary>
public sealed class ViewerSettings
{
    public List<SavedHost> Hosts { get; set; } = [];
    public bool StartFullscreen { get; set; }
    public bool ControlEnabled { get; set; } = true;

    // ------------------------------------------------ modo terceiro monitor

    /// <summary>Host usado no modo teclado/mouse compartilhado.</summary>
    public string? ShareHost { get; set; }

    public int SharePort { get; set; } = Core.Protocol.DefaultPort;

    /// <summary>
    /// Nome do computador do outro lado, aprendido na primeira conexao.
    ///
    /// E o que identifica o notebook de verdade. O IP muda quando o roteador
    /// quer, e quando mudou o modo compartilhado ficou tentando o endereco
    /// velho sem nunca conectar - era preciso ter como reconhece-lo noutro
    /// endereco.
    /// </summary>
    public string? ShareHostName { get; set; }

    /// <summary>Por qual borda do PC o cursor atravessa. Direita e o caso comum.</summary>
    public Core.ScreenEdge ShareEdge { get; set; } = Core.ScreenEdge.Right;

    /// <summary>
    /// Onde a tela do notebook comeca, em fracao da altura do desktop do PC.
    /// Sai da telinha de posicionamento: e o que faz o cursor entrar la na
    /// altura certa em vez de sempre no meio.
    /// </summary>
    public double ShareSpanStart { get; set; }

    /// <summary>Que fracao da altura do desktop do PC a tela do notebook ocupa.</summary>
    public double ShareSpanLength { get; set; } = 1.0;

    /// <summary>
    /// Multiplicador do movimento do mouse enviado ao notebook.
    ///
    /// O deslocamento vai como vem do dispositivo, sem a curva de aceleracao
    /// que o Windows aplica ao cursor local, entao o movimento chega mais
    /// "duro" do lado de la. Em vez de imitar a curva - que depende das
    /// configuracoes de cada maquina - fica este fator, ajustavel na bandeja.
    /// </summary>
    public double ShareSensitivity { get; set; } = 1.0;

    public bool ShareClipboard { get; set; } = true;

    public bool ShareHideCursor { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteMirror");

    private static string FilePath => Path.Combine(Dir, "viewer.json");

    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(FilePath))
                       ?? new ViewerSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preferencias corrompidas nao impedem o viewer de abrir.
        }

        return new ViewerSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* disco cheio ou sem permissao: nao e fatal */ }
    }

    /// <summary>Guarda (ou atualiza) um host com a senha cifrada.</summary>
    public void Remember(string host, int port, string password, string? label)
    {
        var entry = Hosts.FirstOrDefault(h =>
            string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        if (entry is null)
        {
            entry = new SavedHost { Host = host, Port = port };
            Hosts.Add(entry);
        }

        entry.Label = label;
        entry.ProtectedPassword = Protect(password);
        Save();
    }

    public string? PasswordFor(string host, int port)
    {
        var entry = Hosts.FirstOrDefault(h =>
            string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase) && h.Port == port);

        return entry is null ? null : Unprotect(entry.ProtectedPassword);
    }

    private static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
