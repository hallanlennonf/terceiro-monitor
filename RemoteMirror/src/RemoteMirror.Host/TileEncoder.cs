using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteMirror.Core;

namespace RemoteMirror.Host;

/// <summary>Um pedaco de tela que mudou e precisa ir pela rede.</summary>
public readonly record struct TileUpdate(ushort Col, ushort Row, byte[] Jpeg);

/// <summary>
/// Fatia a tela num grid de tiles, detecta quais mudaram e codifica so esses
/// em JPEG.
///
/// E o que torna isto viavel em LAN sem encoder de video: um desktop parado
/// (terminal rodando uma tarefa, por exemplo) gera zero tiles por quadro, e
/// digitar num editor costuma sujar 2 ou 3 tiles. So cena cheia em movimento
/// manda o grid inteiro.
/// </summary>
public sealed class TileEncoder : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _tileSize;
    private readonly ulong[] _hashes;
    private readonly bool[] _seen;
    private readonly Dictionary<(int W, int H), Bitmap> _scratch = [];
    private readonly ImageCodecInfo _jpegCodec;
    private readonly EncoderParameters _encoderParams;

    private bool _disposed;

    public int Cols { get; }
    public int Rows { get; }

    public TileEncoder(int width, int height, int tileSize = Protocol.TileSize)
    {
        _width = width;
        _height = height;
        _tileSize = tileSize;

        Cols = (width + tileSize - 1) / tileSize;
        Rows = (height + tileSize - 1) / tileSize;

        _hashes = new ulong[Cols * Rows];
        _seen = new bool[Cols * Rows];

        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encoderParams = new EncoderParameters(1);
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
    }

    /// <summary>Esquece o estado: o proximo Encode devolve a tela inteira.</summary>
    public void Invalidate() => Array.Clear(_seen);

    /// <summary>
    /// Compara o quadro novo com o anterior e devolve so os tiles alterados.
    /// </summary>
    /// <param name="frame">Buffer BGRA do tamanho da tela.</param>
    /// <param name="quality">Qualidade JPEG, 1 a 100.</param>
    /// <param name="forceAll">true para um keyframe (manda tudo).</param>
    public List<TileUpdate> Encode(byte[] frame, int quality, bool forceAll = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _encoderParams.Param[0]?.Dispose();
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));

        var updates = new List<TileUpdate>();
        var stride = _width * 4;

        for (var row = 0; row < Rows; row++)
        {
            var y0 = row * _tileSize;
            var h = Math.Min(_tileSize, _height - y0);

            for (var col = 0; col < Cols; col++)
            {
                var x0 = col * _tileSize;
                var w = Math.Min(_tileSize, _width - x0);
                var idx = row * Cols + col;

                var hash = HashTile(frame, stride, x0, y0, w, h);

                if (!forceAll && _seen[idx] && _hashes[idx] == hash)
                    continue;

                _hashes[idx] = hash;
                _seen[idx] = true;

                updates.Add(new TileUpdate(
                    (ushort)col,
                    (ushort)row,
                    EncodeTile(frame, stride, x0, y0, w, h)));
            }
        }

        return updates;
    }

    /// <summary>
    /// Hash FNV-1a sobre os pixels do tile, lendo de 8 em 8 bytes.
    /// Nao precisa ser criptografico: so precisa detectar mudanca.
    /// </summary>
    private static unsafe ulong HashTile(byte[] frame, int stride, int x0, int y0, int w, int h)
    {
        const ulong FnvOffset = 14695981039346656037;
        const ulong FnvPrime = 1099511628211;

        var hash = FnvOffset;
        var rowBytes = w * 4;

        fixed (byte* basePtr = frame)
        {
            for (var y = 0; y < h; y++)
            {
                var p = basePtr + (long)(y0 + y) * stride + (long)x0 * 4;
                var i = 0;

                for (; i + 8 <= rowBytes; i += 8)
                {
                    hash ^= *(ulong*)(p + i);
                    hash *= FnvPrime;
                }
                for (; i < rowBytes; i++)
                {
                    hash ^= p[i];
                    hash *= FnvPrime;
                }
            }
        }

        return hash;
    }

    /// <summary>Copia o tile para um Bitmap reaproveitado e salva como JPEG.</summary>
    private byte[] EncodeTile(byte[] frame, int stride, int x0, int y0, int w, int h)
    {
        if (!_scratch.TryGetValue((w, h), out var bmp))
        {
            bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            _scratch[(w, h)] = bmp;
        }

        var data = bmp.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            var rowBytes = w * 4;
            for (var y = 0; y < h; y++)
            {
                var srcOffset = (y0 + y) * stride + x0 * 4;
                var dst = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(frame, srcOffset, dst, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream(4096);
        bmp.Save(ms, _jpegCodec, _encoderParams);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var bmp in _scratch.Values) bmp.Dispose();
        _scratch.Clear();

        _encoderParams.Param[0]?.Dispose();
        _encoderParams.Dispose();
    }
}
