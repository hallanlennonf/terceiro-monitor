using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMirror.Core;

namespace RemoteMirror.Host;

/// <summary>
/// Configuracao do host, em %APPDATA%\RemoteMirror\config.json.
///
/// A senha fica protegida por DPAPI (amarrada a conta do Windows do usuario),
/// entao o arquivo nao entrega a senha nem se for copiado para outra maquina.
/// </summary>
public sealed class HostConfig
{
    /// <summary>Porta TCP onde o host escuta.</summary>
    public int Port { get; set; } = Protocol.DefaultPort;

    /// <summary>Senha em claro. Nao e serializada: vai cifrada em <see cref="ProtectedPassword"/>.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>Senha cifrada com DPAPI, em base64. Este e o campo que vai pro disco.</summary>
    [JsonPropertyName("password")]
    public string? ProtectedPassword { get; set; }

    /// <summary>Se false, o viewer so enxerga: mouse e teclado sao ignorados.</summary>
    public bool AllowControl { get; set; } = true;

    /// <summary>Qualidade JPEG de 1 a 100. 75 e um bom meio-termo em LAN.</summary>
    public int Quality { get; set; } = 75;

    /// <summary>Teto de quadros por segundo.</summary>
    public int MaxFps { get; set; } = 30;

    /// <summary>Indice do monitor capturado por padrao.</summary>
    public int MonitorIndex { get; set; }

    /// <summary>Sobe junto com o Windows.</summary>
    public bool StartWithWindows { get; set; } = true;

    // ------------------------------------------------------------- caminhos

    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteMirror");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    // ------------------------------------------------------------ load/save

    /// <summary>
    /// Carrega a configuracao. Na primeira execucao cria o arquivo e sorteia
    /// uma senha — assim o host nunca fica no ar sem senha por esquecimento.
    /// </summary>
    public static HostConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<HostConfig>(json) ?? new HostConfig();
                cfg.Password = Unprotect(cfg.ProtectedPassword) ?? "";

                if (string.IsNullOrEmpty(cfg.Password))
                {
                    cfg.Password = GeneratePassword();
                    cfg.Save();
                }

                return cfg;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Config corrompida nao pode impedir o host de subir: recomeca limpo.
        }

        var fresh = new HostConfig { Password = GeneratePassword() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        ProtectedPassword = Protect(Password);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>
    /// Senha legivel: 3 grupos de 4 caracteres, sem simbolos ambiguos.
    ///
    /// Pensada para ser ditada no WhatsApp sem confusao entre 0/O e 1/l. Eram
    /// 4 grupos, que ninguem conseguia digitar sem copiar e colar; 14
    /// caracteres de um alfabeto de 32 ainda dao margem de sobra contra
    /// tentativa e erro numa rede local. Quem preferir escolhe a propria senha
    /// no menu da bandeja.
    /// </summary>
    public static string GeneratePassword()
    {
        // Fora: 0/O, 1/I/L, 2/Z e 5/S. Todos confundem quem esta ouvindo a
        // senha pelo telefone, e e assim que ela viaja na pratica.
        const string alphabet = "ABCDEFGHJKMNPQRTUVWXY346789";
        var chars = new char[14];

        for (int i = 0, written = 0; i < chars.Length; i++)
        {
            if (written > 0 && written % 4 == 0 && chars[i - 1] != '-')
            {
                chars[i] = '-';
                continue;
            }

            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            written++;
        }

        return new string(chars);
    }

    // ---------------------------------------------------------------- DPAPI

    private static string? Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Config copiada de outra maquina/usuario: a senha nao abre aqui.
            return null;
        }
    }
}
