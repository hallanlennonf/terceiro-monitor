using RemoteMirror.Host;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Confere o formato da senha sorteada.
///
/// A senha e ditada por telefone e digitada a mao no outro computador, entao
/// o formato nao e cosmetico: um caractere ambiguo ou um grupo a mais e uma
/// conexao que nao acontece.
/// </summary>
internal static class PasswordTests
{
    public static int Run()
    {
        Console.WriteLine("=== RemoteMirror :: senha sorteada ===");
        Console.WriteLine();

        var falhas = 0;
        const string ambiguos = "0O1lI5S2ZzsoLi";

        for (var i = 0; i < 200; i++)
        {
            var senha = HostConfig.GeneratePassword();
            var grupos = senha.Split('-');
            var erros = new List<string>();

            if (senha.Length != 14) erros.Add($"tamanho {senha.Length}, esperado 14");
            if (grupos.Length != 3) erros.Add($"{grupos.Length} grupos, esperado 3");
            if (grupos.Any(g => g.Length != 4)) erros.Add("grupo fora de 4 caracteres");
            if (senha.StartsWith('-') || senha.EndsWith('-')) erros.Add("hifen na ponta");
            if (senha.Contains("--")) erros.Add("hifen duplo");

            // Caracteres que confundem quem ouve a senha pelo telefone. Este
            // teste pegou 2/Z e 5/S ainda no alfabeto, e foram removidos.
            var confusos = senha.Where(c => ambiguos.Contains(c) && c != '-').ToList();
            if (confusos.Count > 0) erros.Add($"caractere ambiguo: {string.Join("", confusos)}");

            if (erros.Count > 0)
            {
                Console.WriteLine($"  [FALHA] {senha}  -> {string.Join("; ", erros)}");
                falhas++;
                if (falhas >= 5) break;
            }
        }

        Console.WriteLine($"  exemplos: {HostConfig.GeneratePassword()}, " +
                          $"{HostConfig.GeneratePassword()}, {HostConfig.GeneratePassword()}");
        Console.WriteLine();

        Console.WriteLine(falhas == 0
            ? "RESULTADO: OK - 200 senhas no formato esperado."
            : $"RESULTADO: FALHOU em {falhas} senha(s).");

        return falhas == 0 ? 0 : 1;
    }
}
