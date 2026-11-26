using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteMirror.Host;

/// <summary>Um monitor disponivel para captura.</summary>
public sealed record MonitorInfo(
    int Index,
    string Name,
    int Width,
    int Height,
    int Left,
    int Top,
    bool IsPrimary);

/// <summary>
/// Captura a tela com a Desktop Duplication API (DXGI).
///
/// E a mesma via que o proprio Windows usa para espelhar telas: le o
/// framebuffer ja composto pela GPU, sem hooks, sem driver de video
/// virtual e - o ponto central aqui - sem criar sessao nova nem bloquear
/// o console. O usuario local continua usando a maquina normalmente.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _duplication = null!;
    private ID3D11Texture2D _staging = null!;

    private byte[] _frameBuffer = [];
    private bool _disposed;

    // Tamanho real da textura entregue pela duplicacao. NAO e necessariamente
    // igual ao tamanho em DesktopCoordinates: tela rotacionada, GPU hibrida ou
    // modo escalado fazem os dois divergirem, e ai o CopyResource copia com
    // tamanhos incompativeis e a imagem sai rasgada.
    private int _textureWidth;
    private int _textureHeight;

    /// <summary>Monitor capturado no momento.</summary>
    public MonitorInfo Monitor { get; private set; } = null!;

    /// <summary>Largura do quadro entregue por <see cref="TryGetFrame"/>.</summary>
    public int Width => _textureWidth;

    /// <summary>Altura do quadro entregue por <see cref="TryGetFrame"/>.</summary>
    public int Height => _textureHeight;

    /// <summary>Rotacao do monitor, quando o painel nao esta na horizontal.</summary>
    public ModeRotation Rotation { get; private set; } = ModeRotation.Identity;

    /// <summary>Bytes por linha no buffer BGRA entregue por <see cref="TryGetFrame"/>.</summary>
    public int Stride => Width * 4;

    public DesktopDuplicator(int monitorIndex)
    {
        Initialize(monitorIndex);
    }

    // ------------------------------------------------------------ enumeracao

    /// <summary>Lista todos os monitores conectados, na ordem do DXGI.</summary>
    public static List<MonitorInfo> ListMonitors()
    {
        var monitors = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        var index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        var bounds = desc.DesktopCoordinates;

                        monitors.Add(new MonitorInfo(
                            Index: index,
                            Name: string.IsNullOrWhiteSpace(desc.DeviceName)
                                ? $"Monitor {index + 1}"
                                : desc.DeviceName.Trim(),
                            Width: bounds.Right - bounds.Left,
                            Height: bounds.Bottom - bounds.Top,
                            Left: bounds.Left,
                            Top: bounds.Top,
                            IsPrimary: bounds.Left == 0 && bounds.Top == 0));

                        index++;
                    }
                }
            }
        }

        if (monitors.Count == 0)
            throw new InvalidOperationException(
                "Nenhum monitor encontrado pelo DXGI. Sessao sem desktop ativo?");

        return monitors;
    }

    // --------------------------------------------------------- inicializacao

    private void Initialize(int monitorIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput? chosenOutput = null;
        MonitorInfo? chosenInfo = null;

        var index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var keepAdapter = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (index == monitorIndex)
                {
                    var desc = output.Description;
                    var bounds = desc.DesktopCoordinates;

                    chosenAdapter = adapter;
                    chosenOutput = output;
                    chosenInfo = new MonitorInfo(
                        index,
                        string.IsNullOrWhiteSpace(desc.DeviceName)
                            ? $"Monitor {index + 1}"
                            : desc.DeviceName.Trim(),
                        bounds.Right - bounds.Left,
                        bounds.Bottom - bounds.Top,
                        bounds.Left,
                        bounds.Top,
                        bounds.Left == 0 && bounds.Top == 0);

                    keepAdapter = true;
                    break;
                }

                output.Dispose();
                index++;
            }

            if (keepAdapter) break;
            adapter.Dispose();
        }

        if (chosenAdapter is null || chosenOutput is null || chosenInfo is null)
            throw new ArgumentOutOfRangeException(
                nameof(monitorIndex), $"Monitor {monitorIndex} nao existe.");

        Monitor = chosenInfo;

        try
        {
            // DriverType.Unknown e obrigatorio quando passamos um adapter explicito.
            var result = D3D11.D3D11CreateDevice(
                chosenAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out _device!,
                out _context!);

            result.CheckError();

            using var output1 = chosenOutput.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);

            // A fonte da verdade e a propria duplicacao, nao as coordenadas do
            // desktop: e ela que diz o tamanho da textura que vamos receber.
            var dupDesc = _duplication.Description;
            Rotation = dupDesc.Rotation;
            _textureWidth = (int)dupDesc.ModeDescription.Width;
            _textureHeight = (int)dupDesc.ModeDescription.Height;

            if (_textureWidth <= 0 || _textureHeight <= 0)
            {
                // Driver nao preencheu o modo: cai para as coordenadas do desktop.
                _textureWidth = Monitor.Width;
                _textureHeight = Monitor.Height;
            }

            // Textura de staging: destino da copia GPU -> memoria legivel pela CPU.
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_textureWidth,
                Height = (uint)_textureHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });

            _frameBuffer = new byte[(long)_textureWidth * _textureHeight * 4];
        }
        finally
        {
            chosenOutput.Dispose();
            chosenAdapter.Dispose();
        }
    }

    // ------------------------------------------------------------- captura

    /// <summary>
    /// Tenta pegar o proximo quadro.
    /// Retorna false quando nada mudou na tela dentro do timeout - o que e o
    /// caso comum e barato: desktop parado nao gasta banda nem CPU.
    /// </summary>
    /// <param name="timeoutMs">Quanto esperar por um quadro novo.</param>
    /// <param name="frame">Buffer BGRA, reaproveitado entre chamadas.</param>
    public bool TryGetFrame(int timeoutMs, out byte[] frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = _frameBuffer;

        IDXGIResource? resource = null;
        try
        {
            var result = _duplication.AcquireNextFrame(
                (uint)timeoutMs, out var frameInfo, out resource);

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                return false;

            if (result.Failure)
            {
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                    throw new DuplicationLostException(
                        "A duplicacao da tela foi perdida (troca de resolucao, " +
                        "modo tela cheia exclusivo ou troca de usuario).");

                result.CheckError();
            }

            // LastPresentTime zero = so o cursor mexeu, imagem nao mudou.
            if (frameInfo.LastPresentTime == 0)
                return false;

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();

            // CopyResource com tamanhos diferentes nao falha de forma visivel:
            // ele produz lixo. Entao conferimos antes, em vez de confiar.
            var desc = texture.Description;
            if (desc.Width != (uint)_textureWidth || desc.Height != (uint)_textureHeight)
            {
                throw new DuplicationLostException(
                    $"O tamanho do quadro mudou: esperava {_textureWidth}x{_textureHeight}, " +
                    $"veio {desc.Width}x{desc.Height}. Recriando a captura.");
            }

            _context.CopyResource(_staging, texture);

            CopyStagingToManaged();
            return true;
        }
        finally
        {
            resource?.Dispose();
            try { _duplication.ReleaseFrame(); } catch { /* ja liberado */ }
        }
    }

    /// <summary>
    /// Copia a textura mapeada para um array gerenciado.
    /// Ler direto da memoria mapeada e muito lento (memoria nao-cacheada),
    /// entao fazemos uma copia sequencial unica e trabalhamos em cima dela.
    /// </summary>
    private unsafe void CopyStagingToManaged()
    {
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var src = (byte*)map.DataPointer;
            var rowBytes = Width * 4;

            fixed (byte* dstBase = _frameBuffer)
            {
                if (map.RowPitch == (uint)rowBytes)
                {
                    Buffer.MemoryCopy(src, dstBase, _frameBuffer.Length, (long)rowBytes * Height);
                }
                else
                {
                    // A GPU alinha as linhas; copiamos linha a linha descartando o padding.
                    for (var y = 0; y < Height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + (long)y * map.RowPitch,
                            dstBase + (long)y * rowBytes,
                            rowBytes,
                            rowBytes);
                    }
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    /// <summary>Posicao do cursor no desktop, em coordenadas deste monitor.</summary>
    public (int X, int Y) GetCursorPosition()
    {
        if (!GetCursorPos(out var p)) return (-1, -1);
        return (p.X - Monitor.Left, p.Y - Monitor.Top);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _staging?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}

/// <summary>
/// A sessao de duplicacao morreu e precisa ser recriada.
/// Acontece em troca de resolucao, jogo em tela cheia exclusiva,
/// Ctrl+Alt+Del ou troca de usuario. O servidor recria e segue.
/// </summary>
public sealed class DuplicationLostException(string message) : Exception(message);

