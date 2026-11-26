using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteMirror.Host;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Verifica o caminho tile -> JPEG -> tile sem precisar de rede nem de duas
/// maquinas.
///
/// Existe por causa de um bug real: os testes de ponta a ponta so usaram um
/// monitor de 1920x1080, e 1920 e multiplo exato de 128, entao as colunas de
/// borda (mais estreitas que um tile) nunca foram exercitadas.
/// </summary>
internal static class TileRoundTrip
{
    public static int Run()
    {
        Console.WriteLine("=== RemoteMirror :: ida e volta dos tiles ===");
        Console.WriteLine();
        Console.WriteLine("Legenda: largura/altura 'quebrada' = nao e multiplo de 128.");
        Console.WriteLine();

        (int W, int H, string Nota)[] casos =
        [
            (1920, 1080, "largura exata, altura quebrada  (o unico ja testado)"),
            (1366,  768, "largura quebrada, altura exata  (notebook tipico)"),
            (1536,  864, "largura exata, altura quebrada"),
            (1600,  900, "largura quebrada, altura quebrada"),
            (1280,  800, "largura exata, altura quebrada"),
            (2560, 1440, "largura exata, altura quebrada"),
            ( 768, 1366, "retrato, altura quebrada"),
            (1920, 1200, "largura exata, altura quebrada"),
        ];

        var falhas = 0;

        foreach (var (w, h, nota) in casos)
        {
            var cols = (w + 127) / 128;
            var rows = (h + 127) / 128;
            var bordaW = w - (cols - 1) * 128;
            var bordaH = h - (rows - 1) * 128;

            Console.WriteLine($"-- {w}x{h}  ({nota})");
            Console.WriteLine($"   grid {cols}x{rows}, tile de borda {bordaW}x{bordaH}");

            var (ok, detalhe) = Verificar(w, h);

            if (ok)
            {
                Console.WriteLine($"   OK   {detalhe}");
            }
            else
            {
                Console.WriteLine($"   FALHA  {detalhe}");
                falhas++;
            }

            Console.WriteLine();
        }

        falhas += ProvarEfeitoDoDpi();

        Console.WriteLine(falhas == 0
            ? "RESULTADO: OK - todas as resolucoes reconstroem corretamente."
            : $"RESULTADO: FALHOU em {falhas} de {casos.Length} resolucoes.");

        return falhas == 0 ? 0 : 1;
    }


    /// <summary>
    /// Demonstra a causa do rasgo em faixas: DrawImageUnscaled usa o tamanho
    /// FISICO da imagem, que depende do DPI. Com o bitmap de destino em 120dpi
    /// e os tiles em 96dpi, cada tile sai 25% maior e invade o vizinho.
    /// DrawImage com retangulo explicito em pixels ignora DPI e acerta.
    /// </summary>
    private static int ProvarEfeitoDoDpi()
    {
        Console.WriteLine("-- efeito do DPI no desenho dos tiles (destino a 120dpi)");

        const int w = 512, h = 384;
        var origem = GerarPadrao(w, h);

        using var encoder = new TileEncoder(w, h);
        var tiles = encoder.Encode(origem, quality: 100, forceAll: true);

        var erroUnscaled = Compor(tiles, w, h, usarRetanguloExplicito: false);
        var erroRetangulo = Compor(tiles, w, h, usarRetanguloExplicito: true);

        Console.WriteLine($"   DrawImageUnscaled        : erro medio {erroUnscaled:F2}");
        Console.WriteLine($"   DrawImage(retangulo)     : erro medio {erroRetangulo:F2}");

        var provado = erroUnscaled > erroRetangulo * 2;
        Console.WriteLine(provado
            ? "   >>> confirmado: Unscaled distorce quando o DPI difere; o retangulo nao."
            : "   (sem diferenca nesta maquina - o DPI do destino coincidiu)");

        // O que importa para o produto e o caminho novo estar correto.
        var ok = erroRetangulo < 6.0;
        Console.WriteLine(ok
            ? "   caminho novo (retangulo) reconstroi corretamente."
            : "   FALHA: o caminho novo tambem esta errado.");
        Console.WriteLine();

        return ok ? 0 : 1;
    }

    private static double Compor(
        IReadOnlyList<RemoteMirror.Host.TileUpdate> tiles, int w, int h, bool usarRetanguloExplicito)
    {
        using var destino = new Bitmap(w, h, PixelFormat.Format32bppPArgb);

        // Simula um destino com DPI diferente do JPEG (que sai a 96dpi).
        destino.SetResolution(120f, 120f);

        using (var g = Graphics.FromImage(destino))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var t in tiles)
            {
                using var ms = new MemoryStream(t.Jpeg);
                using var img = Image.FromStream(ms, false, false);

                if (usarRetanguloExplicito)
                    g.DrawImage(img, new Rectangle(t.Col * 128, t.Row * 128, img.Width, img.Height));
                else
#pragma warning disable CS0618
                    g.DrawImageUnscaled(img, t.Col * 128, t.Row * 128);
#pragma warning restore CS0618
            }
        }

        var origem = GerarPadrao(w, h);
        var data = destino.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            long soma = 0;
            var linha = new byte[data.Stride];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), linha, 0, data.Stride);
                for (var x = 0; x < w; x++)
                    for (var c = 0; c < 3; c++)
                        soma += Math.Abs(origem[(y * w + x) * 4 + c] - linha[x * 4 + c]);
            }
            return soma / (double)(w * h * 3);
        }
        finally { destino.UnlockBits(data); }
    }

    /// <summary>
    /// Gera uma imagem sintetica, passa pelo codificador e remonta do mesmo
    /// jeito que o viewer faz. Devolve se a reconstrucao bate.
    /// </summary>
    private static (bool Ok, string Detalhe) Verificar(int width, int height)
    {
        var origem = GerarPadrao(width, height);

        using var encoder = new TileEncoder(width, height);

        // Qualidade 100 para o JPEG nao ser a causa da diferenca.
        var tiles = encoder.Encode(origem, quality: 100, forceAll: true);

        var esperados = ((width + 127) / 128) * ((height + 127) / 128);
        if (tiles.Count != esperados)
            return (false, $"esperava {esperados} tiles, veio {tiles.Count}");

        using var destino = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(destino))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var t in tiles)
            {
                using var ms = new MemoryStream(t.Jpeg);
                using var img = Image.FromStream(ms, false, false);

                // Mesma chamada do ViewerForm.DrawTiles.
                g.DrawImage(img, new Rectangle(t.Col * 128, t.Row * 128, img.Width, img.Height));
            }
        }

        return Comparar(origem, destino, width, height);
    }

    /// <summary>
    /// Padrao com gradiente horizontal forte: se um tile for parar na coluna
    /// errada, ou for desenhado com largura errada, a diferenca salta.
    /// </summary>
    private static byte[] GerarPadrao(int width, int height)
    {
        var buffer = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                buffer[i + 0] = (byte)(x * 255 / Math.Max(1, width - 1));   // B
                buffer[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));  // G
                buffer[i + 2] = (byte)(((x / 16) + (y / 16)) % 2 == 0 ? 230 : 40); // R xadrez
                buffer[i + 3] = 255;
            }
        }

        return buffer;
    }

    private static (bool Ok, string Detalhe) Comparar(
        byte[] origem, Bitmap destino, int width, int height)
    {
        var data = destino.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

        try
        {
            long somaErro = 0;
            var piorErro = 0;
            var piorX = -1;
            var piorY = -1;
            var pixelsRuins = 0;

            var linha = new byte[data.Stride];

            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), linha, 0, data.Stride);

                for (var x = 0; x < width; x++)
                {
                    var o = (y * width + x) * 4;

                    for (var c = 0; c < 3; c++)
                    {
                        var erro = Math.Abs(origem[o + c] - linha[x * 4 + c]);
                        somaErro += erro;

                        if (erro > piorErro) { piorErro = erro; piorX = x; piorY = y; }
                        if (erro > 40) pixelsRuins++;
                    }
                }
            }

            var medio = somaErro / (double)(width * height * 3);

            // JPEG q=100 ainda e com perdas; erro medio pequeno e esperado.
            // Deslocamento de tile produz erro medio alto e muitos pixels ruins.
            var ok = medio < 6.0 && pixelsRuins < width * height * 3 / 100;

            var detalhe =
                $"erro medio {medio:F2}, pior {piorErro} em ({piorX},{piorY}), " +
                $"pixels ruins {pixelsRuins}";

            return (ok, detalhe);
        }
        finally
        {
            destino.UnlockBits(data);
        }
    }
}
