using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using RemoteMirror.Core;

namespace RemoteMirror.SmokeTest;

/// <summary>
/// Verifica o contrato do modo "terceiro monitor": quando o cliente diz que
/// nao quer video, o host tem que parar de mandar imagem.
///
/// Se isso falhar, o modo compartilhado fica transmitindo a tela inteira sem
/// que ninguem olhe - gastando banda e CPU do notebook a toa.
/// </summary>
internal static class NoVideoTest
{
    public static async Task<int> RunAsync(string host, string password)
    {
        Console.WriteLine("=== RemoteMirror :: modo sem video ===");
        Console.WriteLine();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, Protocol.DefaultPort);
        tcp.NoDelay = true;

        using var channel = await SecureChannel.ConnectAsync(tcp.GetStream(), password);
        Console.WriteLine("[1] conectado");

        // Fase A: com video (padrao). Deve chegar imagem.
        var comVideo = await ContarQuadros(channel, TimeSpan.FromSeconds(3));
        Console.WriteLine($"[2] com video   : {comVideo.Frames} mensagens de imagem, {comVideo.Bytes / 1024} KB");

        // Fase B: pede para desligar o video.
        await Enviar(channel, w => { w.Write((byte)ClientMessage.SetVideoEnabled); w.Write(false); });
        await Task.Delay(700);

        var semVideo = await ContarQuadros(channel, TimeSpan.FromSeconds(3));
        Console.WriteLine($"[3] sem video   : {semVideo.Frames} mensagens de imagem, {semVideo.Bytes / 1024} KB");

        // Fase C: liga de novo, para garantir que da para voltar.
        await Enviar(channel, w => { w.Write((byte)ClientMessage.SetVideoEnabled); w.Write(true); });
        await Task.Delay(700);

        var religado = await ContarQuadros(channel, TimeSpan.FromSeconds(3));
        Console.WriteLine($"[4] religado    : {religado.Frames} mensagens de imagem, {religado.Bytes / 1024} KB");

        Console.WriteLine();

        var ok = true;

        if (comVideo.Frames == 0)
        {
            Console.WriteLine("FALHA: nao chegou imagem nem com video ligado.");
            ok = false;
        }

        if (semVideo.Frames != 0)
        {
            Console.WriteLine($"FALHA: host continuou mandando imagem ({semVideo.Frames} mensagens).");
            ok = false;
        }

        if (religado.Frames == 0)
        {
            Console.WriteLine("FALHA: nao voltou a mandar imagem depois de religar.");
            ok = false;
        }

        Console.WriteLine(ok
            ? "RESULTADO: OK - o host respeita o desligamento do video."
            : "RESULTADO: FALHOU.");

        return ok ? 0 : 1;
    }

    private static async Task<(int Frames, long Bytes)> ContarQuadros(
        SecureChannel channel, TimeSpan duracao)
    {
        var frames = 0;
        long bytes = 0;

        using var cts = new CancellationTokenSource(duracao);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var msg = await channel.ReceiveAsync(cts.Token);
                var tipo = (ServerMessage)msg[0];

                if (tipo is ServerMessage.Keyframe or ServerMessage.Tiles)
                {
                    frames++;
                    bytes += msg.Length;
                }
            }
        }
        catch (OperationCanceledException) { }

        return (frames, bytes);
    }

    private static async Task Enviar(SecureChannel channel, Action<BinaryWriter> build)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) build(w);
        await channel.SendAsync(ms.ToArray());
    }
}
