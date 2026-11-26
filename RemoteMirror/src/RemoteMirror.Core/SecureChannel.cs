using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RemoteMirror.Core;

/// <summary>
/// Canal autenticado e criptografado sobre um Stream (tipicamente TCP).
///
/// Handshake (desafio-resposta mutuo, a senha nunca trafega):
///   host   -> viewer : MAGIC | VERSAO | salt(16) | nonceHost(32)
///   viewer -> host   : nonceViewer(32) | HMAC(k, "RM-client" | nonceHost | nonceViewer)
///   host   -> viewer : HMAC(k, "RM-server" | nonceViewer | nonceHost)
/// onde k = PBKDF2-SHA256(senha, salt, 200k).
///
/// Depois disso cada mensagem vai como:
///   [tamanho BE 4] [contador BE 8] [ciphertext] [tag 16]
/// com AES-256-GCM, nonce = prefixo de direcao (4) | contador (8).
/// Contador por direcao impede replay e reordenacao.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private static readonly byte[] PrefixHostToViewer = [0x52, 0x4D, 0x53, 0x56]; // "RMSV"
    private static readonly byte[] PrefixViewerToHost = [0x52, 0x4D, 0x56, 0x53]; // "RMVS"

    private readonly Stream _stream;
    private readonly AesGcm _aes;
    private readonly byte[] _sendPrefix;
    private readonly byte[] _recvPrefix;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ulong _sendCounter;
    private ulong _recvCounter;
    private bool _disposed;

    private SecureChannel(Stream stream, byte[] sessionKey, bool isHost)
    {
        _stream = stream;
        _aes = new AesGcm(sessionKey, 16);
        _sendPrefix = isHost ? PrefixHostToViewer : PrefixViewerToHost;
        _recvPrefix = isHost ? PrefixViewerToHost : PrefixHostToViewer;
    }

    // ---------------------------------------------------------------- handshake

    /// <summary>Lado do host: aceita um viewer que prove conhecer a senha.</summary>
    public static async Task<SecureChannel> AcceptAsync(
        Stream stream, string password, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonceHost = RandomNumberGenerator.GetBytes(32);

        var hello = new byte[4 + 1 + 16 + 32];
        Protocol.Magic.CopyTo(hello, 0);
        hello[4] = Protocol.Version;
        salt.CopyTo(hello, 5);
        nonceHost.CopyTo(hello, 21);
        await stream.WriteAsync(hello, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var key = DeriveKey(password, salt);

        var reply = new byte[32 + 32];
        await ReadExactAsync(stream, reply, ct).ConfigureAwait(false);
        var nonceViewer = reply[..32];
        var proof = reply[32..];

        var expected = Mac(key, "RM-client", nonceHost, nonceViewer);
        if (!CryptographicOperations.FixedTimeEquals(proof, expected))
            throw new AuthenticationFailedException("Senha incorreta.");

        var answer = Mac(key, "RM-server", nonceViewer, nonceHost);
        await stream.WriteAsync(answer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var sessionKey = Mac(key, "RM-session", nonceHost, nonceViewer);
        return new SecureChannel(stream, sessionKey, isHost: true);
    }

    /// <summary>Lado do viewer: prova a senha e verifica que o host tambem a conhece.</summary>
    public static async Task<SecureChannel> ConnectAsync(
        Stream stream, string password, CancellationToken ct = default)
    {
        var hello = new byte[4 + 1 + 16 + 32];
        await ReadExactAsync(stream, hello, ct).ConfigureAwait(false);

        if (!hello.AsSpan(0, 4).SequenceEqual(Protocol.Magic))
            throw new InvalidDataException("Isso nao parece um host do RemoteMirror.");
        if (hello[4] != Protocol.Version)
            throw new InvalidDataException(
                $"Versao incompativel: host fala v{hello[4]}, viewer fala v{Protocol.Version}. " +
                "Atualize os dois lados.");

        var salt = hello[5..21];
        var nonceHost = hello[21..];
        var key = DeriveKey(password, salt);

        var nonceViewer = RandomNumberGenerator.GetBytes(32);
        var proof = Mac(key, "RM-client", nonceHost, nonceViewer);

        var payload = new byte[64];
        nonceViewer.CopyTo(payload, 0);
        proof.CopyTo(payload, 32);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var answer = new byte[32];
        try
        {
            await ReadExactAsync(stream, answer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // O host fecha a conexao quando a prova nao bate.
            throw new AuthenticationFailedException("Senha incorreta.");
        }

        var expected = Mac(key, "RM-server", nonceViewer, nonceHost);
        if (!CryptographicOperations.FixedTimeEquals(answer, expected))
            throw new AuthenticationFailedException(
                "O host nao provou conhecer a senha. Conexao abortada.");

        var sessionKey = Mac(key, "RM-session", nonceHost, nonceViewer);
        return new SecureChannel(stream, sessionKey, isHost: false);
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Protocol.KdfIterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Mac(byte[] key, string label, byte[] a, byte[] b)
    {
        var labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
        var buffer = new byte[labelBytes.Length + a.Length + b.Length];
        labelBytes.CopyTo(buffer, 0);
        a.CopyTo(buffer, labelBytes.Length);
        b.CopyTo(buffer, labelBytes.Length + a.Length);
        return HMACSHA256.HashData(key, buffer);
    }

    // ---------------------------------------------------------------- mensagens

    /// <summary>Envia uma mensagem. Serializado por um semaforo: pode chamar de varias tasks.</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var counter = _sendCounter++;
            var nonce = BuildNonce(_sendPrefix, counter);

            var frame = new byte[4 + 8 + payload.Length + 16];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), 8 + payload.Length + 16);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(4, 8), counter);

            _aes.Encrypt(
                nonce,
                payload.Span,
                frame.AsSpan(12, payload.Length),
                frame.AsSpan(12 + payload.Length, 16));

            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Recebe a proxima mensagem. Chame de uma unica task por canal.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = new byte[4];
        await ReadExactAsync(_stream, header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length < 8 + 16 || length > Protocol.MaxMessageSize)
            throw new InvalidDataException($"Tamanho de mensagem invalido: {length}.");

        var body = new byte[length];
        await ReadExactAsync(_stream, body, ct).ConfigureAwait(false);

        var counter = BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(0, 8));
        if (counter != _recvCounter)
            throw new InvalidDataException(
                $"Mensagem fora de ordem (esperava {_recvCounter}, veio {counter}).");
        _recvCounter++;

        var nonce = BuildNonce(_recvPrefix, counter);
        var cipherLength = length - 8 - 16;
        var plain = new byte[cipherLength];

        try
        {
            _aes.Decrypt(
                nonce,
                body.AsSpan(8, cipherLength),
                body.AsSpan(8 + cipherLength, 16),
                plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Mensagem corrompida ou adulterada.");
        }

        return plain;
    }

    private static byte[] BuildNonce(byte[] prefix, ulong counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), counter);
        return nonce;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Conexao encerrada pelo outro lado.");
            read += n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aes.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>Senha errada, dos dois lados.</summary>
public sealed class AuthenticationFailedException(string message) : Exception(message);
