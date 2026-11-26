using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteMirror.Core;

/// <summary>
/// Descobre onde o host esta agora, quando o endereco guardado para de servir.
///
/// Existe por um caso real: o notebook pegou outro IP do roteador e o modo
/// terceiro monitor ficou tentando o endereco antigo para sempre, sem nunca
/// conectar. Guardar o IP e inevitavel - e por ele que a conexao comeca - mas
/// tratar o IP como identidade da maquina nao e.
///
/// A ordem das tentativas vai do mais barato ao mais invasivo, e pela ordem em
/// que cada uma costuma funcionar:
///
///   1. o endereco guardado, que no caso comum ainda e o certo;
///   2. a descoberta por UDP, que e o mecanismo desenhado para isto;
///   3. o nome da maquina, resolvido pela rede;
///   4. uma varredura da propria sub-rede na porta do servico.
///
/// O passo 4 parece exagero e nao e: numa maquina com Tailscale, VPN e Hyper-V
/// instalados o broadcast da descoberta se perde entre os adaptadores, e a
/// varredura foi o unico caminho que encontrou o notebook. Ela e limitada a
/// redes /24 privadas e so acontece quando todo o resto falhou.
/// </summary>
public static class HostLocator
{
    /// <summary>Quanto esperar cada tentativa de conexao durante a busca.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Enderecos que valem tentar, em ordem de preferencia e sem repetir.
    ///
    /// Nao confirma identidade: isso quem faz e o handshake, que so passa com
    /// a senha certa. Por isso a lista pode conter candidatos errados sem
    /// risco - no maximo falham na autenticacao.
    /// </summary>
    public static async Task<List<string>> CandidatesAsync(
        string? savedAddress,
        string? savedName,
        int port,
        CancellationToken token = default)
    {
        var ordem = new List<string>();

        void Add(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            if (!ordem.Contains(address, StringComparer.OrdinalIgnoreCase)) ordem.Add(address);
        }

        Add(savedAddress);

        // Se o endereco guardado responde, nem vale procurar mais.
        if (savedAddress is not null && await ProbeAsync(savedAddress, port, token))
            return ordem;

        try
        {
            foreach (var achado in await Discovery.ScanAsync(TimeSpan.FromSeconds(2)))
            {
                // Com o nome guardado, o que casa vem primeiro.
                if (savedName is not null &&
                    string.Equals(achado.Name, savedName, StringComparison.OrdinalIgnoreCase))
                {
                    ordem.Insert(0, achado.Address.ToString());
                }
                else
                {
                    Add(achado.Address.ToString());
                }
            }
        }
        catch { /* descoberta falhou: ha outros caminhos */ }

        if (savedName is not null)
        {
            try
            {
                var entry = await Dns.GetHostAddressesAsync(savedName, token);

                foreach (var ip in entry.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    Add(ip.ToString());
            }
            catch { /* nome nao resolve nesta rede */ }
        }

        // A varredura so e dispensada se algum candidato da PROPRIA rede local
        // ja responde.
        //
        // Olhar "algum candidato" era pouco: numa maquina com Tailscale, o
        // nome do notebook resolve para um endereco da rede virtual, que
        // responde - e a busca parava ali, conectando por um tunel para
        // alcancar uma maquina a dois metros, que pode ainda sair pela
        // internet e voltar por um relay. Cabo e melhor que tunel quando os
        // dois existem.
        if (!await AnyLocalRespondsAsync(ordem, port, token))
        {
            foreach (var ip in await SweepLocalSubnetsAsync(port, token))
                Add(ip);
        }

        // O endereco guardado chegou aqui porque NAO respondeu; tentar a
        // conexao completa nele primeiro so gasta o tempo de espera de quem
        // esta olhando a tela. Vai para o fim, nao fora: a sondagem e curta e
        // pode ter dado falso negativo.
        if (savedAddress is not null && ordem.Count > 1 &&
            ordem.Remove(savedAddress))
        {
            ordem.Add(savedAddress);
        }

        // Rede local primeiro, o resto depois, preservando a ordem relativa.
        // O que sobra - tunel, outra rede - continua valendo como alternativa:
        // e o que permite conectar de fora de casa.
        return
        [
            .. ordem.Where(IsOnLocalSubnet),
            .. ordem.Where(a => !IsOnLocalSubnet(a)),
        ];
    }

    private static async Task<bool> AnyLocalRespondsAsync(
        IEnumerable<string> addresses, int port, CancellationToken token)
    {
        foreach (var a in addresses)
            if (IsOnLocalSubnet(a) && await ProbeAsync(a, port, token)) return true;

        return false;
    }

    /// <summary>O endereco esta numa das sub-redes /24 desta maquina?</summary>
    private static bool IsOnLocalSubnet(string address)
    {
        var corte = address.LastIndexOf('.');
        if (corte <= 0) return false;

        var prefixo = address[..corte];
        return LocalSubnets().Any(r => r.Network == prefixo);
    }

    /// <summary>Alguem aceita conexao neste endereco e porta?</summary>
    public static async Task<bool> ProbeAsync(string address, int port, CancellationToken token)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(ProbeTimeout);

            await tcp.ConnectAsync(address, port, cts.Token);
            return tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Varre as sub-redes /24 locais procurando quem atende na porta.
    ///
    /// So redes privadas e so /24: numa rede maior isto seria lento e
    /// grosseiro, e em rede publica seria indefensavel.
    /// </summary>
    private static async Task<List<string>> SweepLocalSubnetsAsync(int port, CancellationToken token)
    {
        var encontrados = new List<string>();

        foreach (var (rede, meu) in LocalSubnets())
        {
            var tarefas = new List<Task<string?>>();

            for (var ultimo = 1; ultimo <= 254; ultimo++)
            {
                var ip = $"{rede}.{ultimo}";
                if (ip == meu) continue;

                tarefas.Add(ProbeNamedAsync(ip, port, token));
            }

            foreach (var ip in await Task.WhenAll(tarefas))
                if (ip is not null) encontrados.Add(ip);

            if (token.IsCancellationRequested) break;
        }

        return encontrados;
    }

    private static async Task<string?> ProbeNamedAsync(string ip, int port, CancellationToken token) =>
        await ProbeAsync(ip, port, token) ? ip : null;

    /// <summary>Prefixo /24 e endereco proprio de cada interface privada ativa.</summary>
    private static List<(string Network, string Mine)> LocalSubnets()
    {
        var redes = new List<(string, string)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (addr.IPv4Mask?.ToString() != "255.255.255.0") continue;

                var ip = addr.Address.ToString();
                if (!IsPrivate(ip)) continue;

                var prefixo = ip[..ip.LastIndexOf('.')];
                if (!redes.Any(r => r.Item1 == prefixo)) redes.Add((prefixo, ip));
            }
        }

        return redes;
    }

    private static bool IsPrivate(string ip)
    {
        var p = ip.Split('.');
        if (p.Length != 4 || !int.TryParse(p[0], out var a) || !int.TryParse(p[1], out var b))
            return false;

        // 169.254 fica de fora: link-local nao leva a lugar nenhum, e maquina
        // com VPN costuma ter varios desses.
        return a == 10
            || (a == 172 && b >= 16 && b <= 31)
            || (a == 192 && b == 168);
    }
}
