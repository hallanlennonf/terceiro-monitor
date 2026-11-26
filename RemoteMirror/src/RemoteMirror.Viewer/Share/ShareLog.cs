namespace RemoteMirror.Viewer.Share;

/// <summary>
/// Registro do que a conexao tentou e no que falhou.
///
/// Um programa que vive na bandeja e nao conecta nao tem onde se explicar: nao
/// ha console, e caixa de mensagem a cada tentativa seria pior que o problema.
/// Sem este arquivo, "nao conecta" vira adivinhacao - inclusive para quem
/// escreveu o codigo.
/// </summary>
internal static class ShareLog
{
    private static readonly object Lock = new();

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteMirror", "share.log");

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (dir is not null) Directory.CreateDirectory(dir);

                // Corta o arquivo quando passa de 256 KB: e diario de bordo,
                // nao arquivo historico.
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(Path);

                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { /* sem log nao se interrompe o programa */ }
    }
}
