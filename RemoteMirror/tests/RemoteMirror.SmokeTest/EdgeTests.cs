using RemoteMirror.Core;
using RemoteMirror.Host;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Testa a regra de saida do "terceiro monitor" sem cursor real.
/// O risco aqui nao e travar: e o cursor escapar sozinho quando o usuario
/// so queria clicar num botao encostado na lateral da tela.
/// </summary>
internal static class EdgeTests
{
    private static int _falhas;

    public static int Run()
    {
        Console.WriteLine("=== RemoteMirror :: regra de borda ===");
        Console.WriteLine();

        // Host a direita do PC: o cursor entra pela ESQUERDA do host.
        var bounds = new DesktopBounds(0, 0, 1366, 768);

        Caso("entra pela esquerda quando o host esta a direita", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            return d.HostEntrySide == ScreenEdge.Left;
        });

        Caso("entra na mesma altura proporcional (meio)", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            var (x, y) = d.EntryPoint(bounds, 0.5);
            return x == 1 && y == 384;
        });

        Caso("entra no topo quando saiu no topo", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            var (_, y) = d.EntryPoint(bounds, 0.0);
            return y == 0;
        });

        Caso("NAO sai so por encostar na borda", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            return !d.Feed(-5, 0, 0, 400, bounds);
        });

        Caso("NAO sai com movimento para dentro", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            for (var i = 0; i < 50; i++)
                if (d.Feed(10, 0, 500, 400, bounds)) return false;
            return true;
        });

        Caso("sai depois de insistir contra a borda", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            for (var i = 0; i < 4; i++)
                if (d.Feed(-8, 0, 0, 400, bounds)) return i >= 2;
            return false;
        });

        Caso("afastar da borda zera a pressao acumulada", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            d.Feed(-10, 0, 0, 400, bounds);
            d.Feed(-10, 0, 0, 400, bounds);
            if (d.Pressure == 0) return false;

            d.Feed(30, 0, 30, 400, bounds);   // volta para o meio
            if (d.Pressure != 0) return false;

            // Depois disso, um unico toque na borda nao pode sair.
            return !d.Feed(-10, 0, 0, 400, bounds);
        });

        Caso("host a esquerda: sai pela direita", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Left);
            if (d.HostEntrySide != ScreenEdge.Right) return false;

            for (var i = 0; i < 5; i++)
                if (d.Feed(10, 0, 1365, 400, bounds)) return true;
            return false;
        });

        Caso("movimento vertical nao faz sair pela lateral", () =>
        {
            var d = new EdgeExitDetector(ScreenEdge.Right);
            for (var i = 0; i < 50; i++)
                if (d.Feed(0, -10, 0, 0, bounds)) return false;
            return true;
        });

        // Este caso e o que faltava, e por isso a travessia nao funcionava na
        // pratica: encostado na borda o Windows oscila a posicao relatada, e o
        // gesto continuo chega como +1, -1, +1, -1. A versao anterior zerava a
        // pressao em cada -1 e nunca alcancava o limite, por mais que a pessoa
        // empurrasse.
        Caso("oscilacao de +1/-1 na borda ainda atravessa", () =>
        {
            // Lado do PC: a pessoa empurra contra a borda DIREITA daqui.
            var d = EdgeExitDetector.PushingAgainst(ScreenEdge.Right);

            for (var i = 0; i < 200; i++)
            {
                var dx = i % 2 == 0 ? 1 : -1;
                if (d.Feed(dx, 0, 1365, 400, bounds)) return true;
            }

            return false;
        });

        // A folga contra a oscilacao nao pode virar uma porta aberta: um
        // movimento de verdade para longe da borda tem que desistir.
        Caso("movimento real para longe ainda zera a pressao", () =>
        {
            var d = EdgeExitDetector.PushingAgainst(ScreenEdge.Right);

            for (var i = 0; i < 500; i++)
            {
                if (d.Feed(10, 0, 1365, 400, bounds)) return false;  // empurra
                if (d.Feed(-20, 0, 1300, 400, bounds)) return false; // desiste
            }

            return true;
        });

        // A folga contra oscilacao, sozinha, abria uma porta: o ruido de +1 do
        // cursor parado na borda somava devagar ate passar do limite, e o
        // cursor atravessava sozinho so por estar descansando ali. O prazo da
        // pressao e o que separa ruido espacado de gesto continuo.
        Caso("ruido espacado na borda NAO atravessa", () =>
        {
            var d = EdgeExitDetector.PushingAgainst(ScreenEdge.Right);
            long t = 0;

            for (var i = 0; i < 500; i++)
            {
                t += 1000;  // um tremor por segundo
                if (d.Feed(1, 0, 1365, 400, bounds, t)) return false;
            }

            return true;
        });

        Caso("gesto continuo atravessa dentro do prazo", () =>
        {
            var d = EdgeExitDetector.PushingAgainst(ScreenEdge.Right);
            long t = 0;

            for (var i = 0; i < 30; i++)
            {
                t += 10;  // movimento de um mouse de verdade
                if (d.Feed(4, 0, 1365, 400, bounds, t)) return true;
            }

            return false;
        });

        Caso("oscilacao durante gesto continuo ainda atravessa", () =>
        {
            var d = EdgeExitDetector.PushingAgainst(ScreenEdge.Right);
            long t = 0;

            for (var i = 0; i < 200; i++)
            {
                t += 8;
                var dx = i % 2 == 0 ? 2 : -1;
                if (d.Feed(dx, 0, 1365, 400, bounds, t)) return true;
            }

            return false;
        });

        Console.WriteLine();
        Console.WriteLine(_falhas == 0
            ? "RESULTADO: OK - regra de borda consistente."
            : $"RESULTADO: FALHOU em {_falhas} caso(s).");

        return _falhas == 0 ? 0 : 1;
    }

    private static void Caso(string nome, Func<bool> teste)
    {
        bool ok;
        try { ok = teste(); }
        catch (Exception ex) { ok = false; nome += $"  ({ex.GetType().Name})"; }

        Console.WriteLine($"  [{(ok ? "ok " : "FALHA")}] {nome}");
        if (!ok) _falhas++;
    }
}
