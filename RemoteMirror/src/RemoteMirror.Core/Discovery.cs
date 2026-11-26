using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RemoteMirror.Core;

/// <summary>Um host encontrado na rede local.</summary>
public sealed record DiscoveredHost(string Name, IPAddress Address, int Port)
{
    public override string ToString() => $"{Name}  ({Address}:{Port})";
}

/// <summary>
/// Descoberta automatica na LAN via UDP broadcast, para o colega nao
/// precisar decorar IP. Nenhum segredo trafega aqui: a resposta so diz
/// "existe um host chamado X em tal porta". A senha continua sendo exigida
/// no handshake TCP.
/// </summary>
public static class Discovery
{
    /// <summary>Porta UDP usada so para descoberta.</summary>
    public const int Port = 7702;

    private static readonly byte[] Probe = "RMIR?"u8.ToArray();
    private static readonly byte[] ReplyMagic = "RMIR!"u8.ToArray();

    // ------------------------------------------------------------------- host

    /// <summary>
    /// Fica ouvindo probes e respondendo quem pergunta. Roda no host.
    /// </summary>
    public sealed class Responder : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _name;
        private readonly int _tcpPort;

        public Responder(string name, int tcpPort)
        {
            _name = name;
            _tcpPort = tcpPort;

            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _udp.EnableBroadcast = true;

            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            var reply = BuildReply(_name, _tcpPort);

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    if (result.Buffer.AsSpan().StartsWith(Probe))
                        await _udp.SendAsync(reply, reply.Length, result.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { /* pacote torto, segue ouvindo */ }
            }
        }

        private static byte[] BuildReply(string name, int tcpPort)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 200) nameBytes = nameBytes[..200];

            var buffer = new byte[ReplyMagic.Length + 1 + 2 + 1 + nameBytes.Length];
            var i = 0;
            ReplyMagic.CopyTo(buffer, i); i += ReplyMagic.Length;
            buffer[i++] = Protocol.Version;
            buffer[i++] = (byte)(tcpPort >> 8);
            buffer[i++] = (byte)(tcpPort & 0xFF);
            buffer[i++] = (byte)nameBytes.Length;
            nameBytes.CopyTo(buffer, i);
            return buffer;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
            _cts.Dispose();
        }
    }

    // ----------------------------------------------------------------- viewer

    /// <summary>
    /// Varre a rede local procurando hosts. Roda no viewer.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredHost>> ScanAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredHost>();

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;

        foreach (var target in BroadcastTargets())
        {
            try { await udp.SendAsync(Probe, Probe.Length, target).ConfigureAwait(false); }
            catch (SocketException) { /* interface sem rota, ignora */ }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (TryParseReply(result.Buffer, result.RemoteEndPoint.Address, out var host))
                    found[$"{host.Address}:{host.Port}"] = host;
            }
        }
        catch (OperationCanceledException) { /* fim do tempo de varredura */ }
        catch (SocketException) { }

        return found.Values.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryParseReply(byte[] data, IPAddress from, out DiscoveredHost host)
    {
        host = null!;
        if (data.Length < ReplyMagic.Length + 4) return false;
        if (!data.AsSpan().StartsWith(ReplyMagic)) return false;

        var i = ReplyMagic.Length;
        var version = data[i++];
        if (version != Protocol.Version) return false;

        var port = (data[i++] << 8) | data[i++];
        var nameLen = data[i++];
        if (i + nameLen > data.Length) return false;

        var name = Encoding.UTF8.GetString(data, i, nameLen);
        host = new DiscoveredHost(name, from, port);
        return true;
    }

    /// <summary>
    /// Broadcast global mais o broadcast dirigido de cada interface ativa.
    /// Algumas redes descartam o 255.255.255.255, outras descartam o dirigido;
    /// mandar nos dois cobre os dois casos.
    /// </summary>
    private static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        yield return new IPEndPoint(IPAddress.Broadcast, Port);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null) continue;

                var addr = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;

                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(addr[i] | (byte)~mask[i]);

                yield return new IPEndPoint(new IPAddress(broadcast), Port);
            }
        }
    }
}
