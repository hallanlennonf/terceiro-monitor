using RemoteMirror.Host;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Compara, para cada monitor, o tamanho que o Windows reporta no desktop com
/// o tamanho real da textura que a duplicacao entrega.
///
/// Quando os dois divergem, o CopyResource copia com tamanhos incompativeis e
/// a imagem chega rasgada em faixas. Este relatorio existe para mostrar a
/// divergencia em vez de adivinhar.
/// </summary>
internal static class MonitorReport
{
    public static int Run()
    {
        Console.WriteLine("=== RemoteMirror :: monitores ===");
        Console.WriteLine();

        var divergentes = 0;

        foreach (var m in DesktopDuplicator.ListMonitors())
        {
            Console.WriteLine($"[{m.Index}] {m.Name}");
            Console.WriteLine($"     desktop     : {m.Width}x{m.Height} em ({m.Left},{m.Top})");

            try
            {
                using var dup = new DesktopDuplicator(m.Index);

                Console.WriteLine($"     duplicacao  : {dup.Width}x{dup.Height}");
                Console.WriteLine($"     rotacao     : {dup.Rotation}");

                if (dup.Width != m.Width || dup.Height != m.Height)
                {
                    divergentes++;
                    Console.WriteLine("     >>> DIVERGEM - este monitor quebraria antes da correcao");
                }
                else
                {
                    Console.WriteLine("     batem");
                }

                if (dup.TryGetFrame(400, out var frame))
                    Console.WriteLine($"     quadro      : {frame.Length} bytes (esperado {(long)dup.Width * dup.Height * 4})");
                else
                    Console.WriteLine("     quadro      : sem mudanca na tela (timeout, normal)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     ERRO: {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine(divergentes == 0
            ? "Nenhuma divergencia nesta maquina."
            : $"{divergentes} monitor(es) com divergencia.");

        return 0;
    }
}
