using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteMirror.Core;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Cliente headless que exercita o caminho completo contra um host real:
/// descoberta UDP, handshake cifrado, keyframe, tiles incrementais.
/// Salva um PNG do que recebeu, para dar para conferir a olho.
///
///   RemoteMirrorSmokeTest.exe [host] [saida.png] [segundos]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--tiles")
            return TileRoundTrip.Run();

        if (args.Length > 0 && args[0] == "--monitores")
            return MonitorReport.Run();

        if (args.Length > 0 && args[0] == "--borda")
            return EdgeTests.Run();

        if (args.Length > 0 && args[0] == "--senha")
            return PasswordTests.Run();

        if (args.Length > 0 && args[0] == "--descobrir")
            return await DiscoveryProbe.RunAsync();

        if (args.Length > 0 && args[0] == "--achar")
            return await LocatorProbe.RunAsync(
                args.Length > 1 ? args[1] : "192.168.18.230",
                args.Length > 2 ? args[2] : null);

        if (args.Length > 0 && args[0] == "--semvideo")
        {
            var senha = ReadLocalPassword();
            if (senha is null)
            {
                Console.WriteLine("Nao consegui ler a senha local. O host ja rodou aqui?");
                return 1;
            }
            return await NoVideoTest.RunAsync(args.Length > 1 ? args[1] : "127.0.0.1", senha);
        }

        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var outPng = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "rm-smoke.png");
        var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 5;

        Console.WriteLine("=== RemoteMirror :: teste de fumaca ===");
        Console.WriteLine();

        // ------------------------------------------------- 1. descoberta UDP
        Console.WriteLine("[1] Descoberta UDP na rede local...");
        try
        {
            var found = await Discovery.ScanAsync(TimeSpan.FromSeconds(2));
            if (found.Count == 0)
                Console.WriteLine("    nenhum host respondeu (ok se o firewall UDP estiver fechado)");
            foreach (var h in found)
                Console.WriteLine($"    achou: {h.Name} em {h.Address}:{h.Port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    falhou: {ex.Message}");
        }

        // -------------------------------------------------- 2. senha (DPAPI)
        Console.WriteLine();
        Console.WriteLine("[2] Lendo a senha do config local (DPAPI)...");
        var password = ReadLocalPassword();
        if (password is null)
        {
            Console.WriteLine("    FALHOU: nao consegui ler a senha do host.");
            Console.WriteLine("    O host ja rodou ao menos uma vez nesta conta?");
            return 1;
        }
        Console.WriteLine($"    senha obtida ({password.Length} caracteres)");

        // ------------------------------------------- 3. handshake com senha errada
        Console.WriteLine();
        Console.WriteLine("[3] Handshake com senha ERRADA (deve ser recusado)...");
        try
        {
            using var bad = new TcpClient();
            await bad.ConnectAsync(host, Protocol.DefaultPort);
            using var _ = await SecureChannel.ConnectAsync(bad.GetStream(), "SENHA-ERRADA-XXXX");
            Console.WriteLine("    FALHOU: o host aceitou uma senha errada!");
            return 1;
        }
        catch (AuthenticationFailedException)
        {
            Console.WriteLine("    recusado corretamente");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    recusado ({ex.GetType().Name})");
        }

        // ------------------------------------------- 4. sessao real
        Console.WriteLine();
        Console.WriteLine($"[4] Conectando em {host}:{Protocol.DefaultPort} com a senha certa...");

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, Protocol.DefaultPort);
        tcp.NoDelay = true;

        using var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), password);
        Console.WriteLine("    handshake ok, canal cifrado estabelecido");

        Bitmap? surface = null;
        var tileSize = Protocol.TileSize;
        var keyframes = 0;
        var deltaMessages = 0;
        var tilesTotal = 0;
        long bytesTotal = 0;
        var cursorUpdates = 0;
        double? latency = null;

        var clock = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        // Ping a cada 500ms: a primeira medida sai atras do keyframe e nao
        // representa o regime normal, entao guardamos todas e olhamos a mediana.
        var latencies = new List<double>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await SendAsync(channel, w =>
                    {
                        w.Write((byte)ClientMessage.Ping);
                        w.Write(Stopwatch.GetTimestamp());
                    });
                    await Task.Delay(500, cts.Token);
                }
            }
            catch { }
        });

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var msg = await channel.ReceiveAsync(cts.Token);
                bytesTotal += msg.Length;

                using var ms = new MemoryStream(msg);
                using var r = new BinaryReader(ms, Encoding.UTF8);
                var type = (ServerMessage)r.ReadByte();

                switch (type)
                {
                    case ServerMessage.ServerInfo:
                    {
                        var count = r.ReadInt32();
                        Console.WriteLine($"    ServerInfo: {count} monitor(es)");
                        for (var i = 0; i < count; i++)
                        {
                            var name = r.ReadString();
                            var w = r.ReadInt32();
                            var h = r.ReadInt32();
                            var primary = r.ReadBoolean();
                            Console.WriteLine($"      [{i}] {name} {w}x{h}{(primary ? " (principal)" : "")}");
                        }
                        var current = r.ReadInt32();
                        var control = r.ReadBoolean();
                        var machine = r.ReadString();
                        Console.WriteLine($"      capturando o {current}, controle={control}, maquina={machine}");
                        break;
                    }

                    case ServerMessage.Keyframe:
                    {
                        var monitorIndex = r.ReadInt32();
                        var width = r.ReadInt32();
                        var height = r.ReadInt32();
                        tileSize = r.ReadInt32();

                        surface?.Dispose();
                        surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                        var n = DrawTiles(r, surface, tileSize);
                        keyframes++;
                        tilesTotal += n;

                        Console.WriteLine(
                            $"    KEYFRAME: {width}x{height}, tile={tileSize}, " +
                            $"{n} tiles, {msg.Length / 1024.0:F0} KB  (+{clock.ElapsedMilliseconds}ms)");
                        break;
                    }

                    case ServerMessage.Tiles:
                    {
                        if (surface is null) break;
                        var n = DrawTiles(r, surface, tileSize);
                        deltaMessages++;
                        tilesTotal += n;
                        break;
                    }

                    case ServerMessage.Cursor:
                        r.ReadInt32(); r.ReadInt32();
                        cursorUpdates++;
                        break;

                    case ServerMessage.Pong:
                        latency = Stopwatch.GetElapsedTime(r.ReadInt64()).TotalMilliseconds;
                        latencies.Add(latency.Value);
                        break;

                    case ServerMessage.Notice:
                        Console.WriteLine($"    aviso do host: {r.ReadString()}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"    ERRO no recebimento: {ex.Message}");
            return 1;
        }

        clock.Stop();

        // ------------------------------------------------------ 5. resultado
        Console.WriteLine();
        Console.WriteLine("[5] Resultado");
        Console.WriteLine($"    duracao            : {clock.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"    keyframes          : {keyframes}");
        Console.WriteLine($"    quadros parciais   : {deltaMessages}");
        Console.WriteLine($"    tiles recebidos    : {tilesTotal}");
        Console.WriteLine($"    trafego            : {bytesTotal / 1024.0:F0} KB " +
                          $"({bytesTotal / 1024.0 / clock.Elapsed.TotalSeconds:F0} KB/s)");
        Console.WriteLine($"    updates de cursor  : {cursorUpdates}");
        if (latencies.Count > 0)
        {
            var sorted = latencies.OrderBy(v => v).ToList();
            Console.WriteLine($"    latencia: 1a medida {latencies[0]:F1}ms (atras do keyframe)");
            Console.WriteLine($"              mediana   {sorted[sorted.Count / 2]:F1}ms  " +
                              $"min {sorted[0]:F1}ms  max {sorted[^1]:F1}ms  ({latencies.Count} amostras)");
        }
        else
        {
            Console.WriteLine("    latencia           : nao medida");
        }

        if (surface is null)
        {
            Console.WriteLine();
            Console.WriteLine("RESULTADO: FALHOU — nenhum keyframe chegou.");
            return 1;
        }

        surface.Save(outPng, ImageFormat.Png);
        Console.WriteLine($"    imagem salva em    : {outPng}");

        var blank = IsMostlyBlank(surface);
        Console.WriteLine($"    imagem em branco?  : {(blank ? "SIM (suspeito)" : "nao")}");

        surface.Dispose();

        Console.WriteLine();
        if (keyframes > 0 && !blank)
        {
            Console.WriteLine("RESULTADO: OK — pipeline completo funcionando.");
            return 0;
        }

        Console.WriteLine("RESULTADO: FALHOU — recebeu quadro, mas a imagem nao tem conteudo.");
        return 1;
    }

    private static int DrawTiles(BinaryReader r, Bitmap surface, int tileSize)
    {
        var count = r.ReadInt32();
        using var g = Graphics.FromImage(surface);

        for (var i = 0; i < count; i++)
        {
            var col = r.ReadUInt16();
            var row = r.ReadUInt16();
            var len = r.ReadInt32();
            var jpeg = r.ReadBytes(len);

            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms);
            g.DrawImageUnscaled(img, col * tileSize, row * tileSize);
        }

        return count;
    }

    /// <summary>Amostra o bitmap para detectar uma tela toda preta/branca.</summary>
    private static bool IsMostlyBlank(Bitmap bmp)
    {
        var distinct = new HashSet<int>();

        for (var y = 0; y < bmp.Height; y += 37)
        for (var x = 0; x < bmp.Width; x += 37)
        {
            distinct.Add(bmp.GetPixel(x, y).ToArgb());
            if (distinct.Count > 12) return false;
        }

        return true;
    }

    private static async Task SendAsync(SecureChannel channel, Action<BinaryWriter> build)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) build(w);
        await channel.SendAsync(ms.ToArray());
    }

    /// <summary>Le e decifra a senha gravada pelo host nesta mesma conta.</summary>
    private static string? ReadLocalPassword()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteMirror", "config.json");

            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("password", out var prop)) return null;

            var protectedValue = prop.GetString();
            if (string.IsNullOrEmpty(protectedValue)) return null;

            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }
}
