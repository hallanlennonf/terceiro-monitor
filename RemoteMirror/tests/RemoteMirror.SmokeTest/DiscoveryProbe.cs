using RemoteMirror.Core;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Lista quem esta anunciando o RemoteMirror na rede local.
///
/// Serve para o caso em que o notebook troca de IP: o endereco salvo para de
/// responder e a pergunta passa a ser "onde ele esta agora", nao "porque nao
/// conecta".
/// </summary>
internal static class DiscoveryProbe
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== RemoteMirror :: descoberta na rede ===");
        Console.WriteLine($"porta de descoberta: UDP {Discovery.Port}");
        Console.WriteLine();
        Console.WriteLine("procurando por 4 segundos...");
        Console.WriteLine();

        var achados = await Discovery.ScanAsync(TimeSpan.FromSeconds(4));

        if (achados.Count == 0)
        {
            Console.WriteLine("Nada encontrado.");
            Console.WriteLine();
            Console.WriteLine("Isso acontece quando o host nao esta rodando no outro");
            Console.WriteLine("computador, quando ele esta em outra rede, ou quando o");
            Console.WriteLine("firewall esta bloqueando o UDP da descoberta.");
            return 1;
        }

        foreach (var h in achados)
            Console.WriteLine($"  {h.Name,-20} {h.Address}:{h.Port}");

        Console.WriteLine();
        Console.WriteLine($"RESULTADO: {achados.Count} computador(es) encontrado(s).");

        return 0;
    }
}
