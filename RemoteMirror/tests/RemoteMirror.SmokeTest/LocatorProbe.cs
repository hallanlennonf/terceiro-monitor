using System.Diagnostics;
using RemoteMirror.Core;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Exercita a busca do host com um endereco propositalmente errado.
///
/// E o cenario que quebrou na pratica: o notebook pegou outro IP e o modo
/// terceiro monitor ficou preso no endereco antigo.
/// </summary>
internal static class LocatorProbe
{
    public static async Task<int> RunAsync(string enderecoVelho, string? nome)
    {
        Console.WriteLine("=== RemoteMirror :: achar o host ===");
        Console.WriteLine($"endereco guardado (supostamente velho): {enderecoVelho}");
        Console.WriteLine($"nome guardado: {nome ?? "(nenhum)"}");
        Console.WriteLine();

        var relogio = Stopwatch.StartNew();
        var candidatos = await HostLocator.CandidatesAsync(enderecoVelho, nome, Protocol.DefaultPort);
        relogio.Stop();

        Console.WriteLine($"candidatos em {relogio.ElapsedMilliseconds} ms:");
        foreach (var c in candidatos) Console.WriteLine($"  {c}");
        Console.WriteLine();

        Console.WriteLine("quem realmente atende:");
        var vivos = 0;

        foreach (var c in candidatos)
        {
            var ok = await HostLocator.ProbeAsync(c, Protocol.DefaultPort, CancellationToken.None);
            Console.WriteLine($"  {c,-16} {(ok ? "ATENDE" : "silencio")}");
            if (ok) vivos++;
        }

        Console.WriteLine();

        if (vivos > 0)
        {
            Console.WriteLine("RESULTADO: OK - a busca encontrou um host vivo mesmo com o " +
                              "endereco guardado errado.");
            return 0;
        }

        Console.WriteLine("RESULTADO: nao achou ninguem. O host esta rodando no outro lado?");
        return 1;
    }
}
