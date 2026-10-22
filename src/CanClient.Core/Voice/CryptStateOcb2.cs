using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CanClient.Core.Voice;

/// <summary>
/// Mumble 的 UDP 加密：AES-128-OCB2，逐条照 Mumble 的 CryptState（和 pymumble 的 crypto.py）。
/// 密钥和两个 nonce 由服务器在 CryptSetup 里给。包头 4 字节：IV 的最低字节 + 认证标签的前 3 字节。
/// 带上了 https://eprint.iacr.org/2019/311 第 9 节的防御（拒绝可被利用的明文/密文块）。
/// </summary>
public sealed class CryptStateOcb2 : IDisposable
{
    public const int BlockSize = 16;

    private Aes? _aes;
    private byte[] _key = new byte[BlockSize];
    private byte[] _encryptIv = new byte[BlockSize];
    private byte[] _decryptIv = new byte[BlockSize];
    private readonly byte[] _decryptHistory = new byte[256];

    public int Good { get; private set; }
    public int Late { get; private set; }
    public int Lost { get; private set; }
    public long LastGoodTicks { get; private set; }

    public bool IsValid => _aes is not null;

    public byte[] EncryptIv => (byte[])_encryptIv.Clone();

    public void SetKey(byte[] key, byte[] encryptIv, byte[] decryptIv)
    {
        if (key.Length != BlockSize || encryptIv.Length != BlockSize || decryptIv.Length != BlockSize)
            throw new ArgumentException("OCB2 key and nonces must be 16 bytes");
        _key = (byte[])key.Clone();
        _encryptIv = (byte[])encryptIv.Clone();
        _decryptIv = (byte[])decryptIv.Clone();
        Array.Clear(_decryptHistory);
        _aes?.Dispose();
        _aes = Aes.Create();
        _aes.Key = _key;
    }

    public void SetDecryptIv(byte[] iv)
    {
        if (iv.Length != BlockSize)
            throw new ArgumentException("nonce must be 16 bytes");
        _decryptIv = (byte[])iv.Clone();
        Array.Clear(_decryptHistory);
    }

    public void Dispose() => _aes?.Dispose();

    private void AesEncrypt(ReadOnlySpan<byte> input, Span<byte> output) =>
        _aes!.EncryptEcb(input, output, PaddingMode.None);

    private void AesDecrypt(ReadOnlySpan<byte> input, Span<byte> output) =>
        _aes!.DecryptEcb(input, output, PaddingMode.None);

    private static void Increment(byte[] iv, int start = 0)
    {
        for (var i = start; i < BlockSize; i++)
        {
            iv[i]++;
            if (iv[i] != 0)
                break;
        }
    }

    private static void Decrement(byte[] iv, int start = 0)
    {
        for (var i = start; i < BlockSize; i++)
        {
            iv[i]--;
            if (iv[i] != 0xFF)
                break;
        }
    }

    private static void Xor(Span<byte> dst, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < BlockSize; i++)
            dst[i] = (byte)(a[i] ^ b[i]);
    }

    /// <summary>S2：把 128 位大端数左移一位，溢出的最高位反馈成 0x87。</summary>
    private static void S2(Span<byte> block)
    {
        var hi = BinaryPrimitives.ReadUInt64BigEndian(block);
        var lo = BinaryPrimitives.ReadUInt64BigEndian(block[8..]);
        var carry = hi >> 63;
        BinaryPrimitives.WriteUInt64BigEndian(block, (hi << 1) | (lo >> 63));
        BinaryPrimitives.WriteUInt64BigEndian(block[8..], (lo << 1) ^ (carry * 0x87));
    }

    /// <summary>加密一个 UDP 包。返回 null 表示这个明文会产生可被利用的块，丢掉别发。</summary>
    public byte[]? Encrypt(ReadOnlySpan<byte> plain)
    {
        if (_aes is null)
            return null;
        Increment(_encryptIv);
        var output = new byte[plain.Length + 4];
        Span<byte> tag = stackalloc byte[BlockSize];
        if (!OcbEncrypt(plain, output.AsSpan(4), _encryptIv, tag))
            return null;
        output[0] = _encryptIv[0];
        output[1] = tag[0];
        output[2] = tag[1];
        output[3] = tag[2];
        return output;
    }

    internal bool OcbEncrypt(ReadOnlySpan<byte> plain, Span<byte> encrypted, ReadOnlySpan<byte> nonce, Span<byte> tag)
    {
        Span<byte> delta = stackalloc byte[BlockSize];
        Span<byte> checksum = stackalloc byte[BlockSize];
        Span<byte> tmp = stackalloc byte[BlockSize];
        Span<byte> pad = stackalloc byte[BlockSize];
        Span<byte> lastPlain = stackalloc byte[BlockSize];
        checksum.Clear();
        AesEncrypt(nonce, delta);

        var pos = 0;
        var remaining = plain.Length;
        var hadFullBlock = false;
        while (remaining > BlockSize)
        {
            var block = plain.Slice(pos, BlockSize);
            S2(delta);
            Xor(tmp, delta, block);
            AesEncrypt(tmp, tmp);
            Xor(encrypted.Slice(pos, BlockSize), delta, tmp);
            Xor(checksum, checksum, block);
            block.CopyTo(lastPlain);
            hadFullBlock = true;
            pos += BlockSize;
            remaining -= BlockSize;
        }

        // 倒数第二块除了最后一字节全是 0 时可以被攻击，这种包不发
        if (hadFullBlock && !lastPlain[..^1].ContainsAnyExcept((byte)0))
            return false;

        S2(delta);
        tmp.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(tmp[8..], (ulong)remaining * 8);
        Xor(tmp, tmp, delta);
        AesEncrypt(tmp, pad);
        // 明文块 = 剩下的明文 + pad 的尾巴
        plain.Slice(pos, remaining).CopyTo(tmp);
        pad[remaining..].CopyTo(tmp[remaining..]);
        Xor(checksum, checksum, tmp);
        Xor(tmp, pad, tmp);
        tmp[..remaining].CopyTo(encrypted.Slice(pos, remaining));

        S2Into(delta, pad);
        Xor(delta, delta, pad);
        Xor(tmp, delta, checksum);
        AesEncrypt(tmp, tag);
        return true;
    }

    private static void S2Into(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        S2(destination);
    }

    /// <summary>解密一个 UDP 包。失败（乱序太多、重放、被篡改）返回 null。</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> source)
    {
        if (_aes is null || source.Length < 4)
            return null;
        var plainLength = source.Length - 4;
        var div = (byte[])_decryptIv.Clone();
        var ivByte = source[0];
        var late = false;
        var lost = 0;

        if (((div[0] + 1) & 0xFF) == ivByte)
        {
            // 按顺序来的
            if (ivByte > div[0])
                div[0] = ivByte;
            else if (ivByte < div[0])
            {
                div[0] = ivByte;
                Increment(div, 1);
            }
            else
                return null;
        }
        else
        {
            var diff = ivByte - div[0];
            if (diff > 128)
                diff -= 256;
            else if (diff < -128)
                diff += 256;

            if (ivByte < div[0] && diff is > -30 and < 0)
            {
                late = true;
                lost = -1;
                div[0] = ivByte;
            }
            else if (ivByte > div[0] && diff is > -30 and < 0)
            {
                late = true;
                lost = -1;
                div[0] = ivByte;
                Decrement(div, 1);
            }
            else if (ivByte > div[0] && diff > 0)
            {
                lost = ivByte - div[0] - 1;
                div[0] = ivByte;
            }
            else if (ivByte < div[0] && diff > 0)
            {
                lost = 0x100 - div[0] + ivByte - 1;
                div[0] = ivByte;
                Increment(div, 1);
            }
            else
                return null;

            if (_decryptHistory[div[0]] == div[1])
                return null;
        }

        var plain = new byte[plainLength];
        Span<byte> tag = stackalloc byte[BlockSize];
        if (!OcbDecrypt(source[4..], plain, div, tag))
            return null;
        if (tag[0] != source[1] || tag[1] != source[2] || tag[2] != source[3])
            return null;

        _decryptHistory[div[0]] = div[1];
        if (!late)
            _decryptIv = div;
        else
            Late++;
        Good++;
        Lost += lost;
        LastGoodTicks = Stopwatch.GetTimestamp();
        return plain;
    }

    internal bool OcbDecrypt(ReadOnlySpan<byte> encrypted, Span<byte> plain, ReadOnlySpan<byte> nonce, Span<byte> tag)
    {
        Span<byte> delta = stackalloc byte[BlockSize];
        Span<byte> checksum = stackalloc byte[BlockSize];
        Span<byte> tmp = stackalloc byte[BlockSize];
        Span<byte> pad = stackalloc byte[BlockSize];
        checksum.Clear();
        AesEncrypt(nonce, delta);

        var pos = 0;
        var remaining = plain.Length;
        while (remaining > BlockSize)
        {
            S2(delta);
            Xor(tmp, delta, encrypted.Slice(pos, BlockSize));
            AesDecrypt(tmp, tmp);
            Xor(plain.Slice(pos, BlockSize), delta, tmp);
            Xor(checksum, checksum, plain.Slice(pos, BlockSize));
            pos += BlockSize;
            remaining -= BlockSize;
        }

        S2(delta);
        tmp.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(tmp[8..], (ulong)remaining * 8);
        Xor(tmp, tmp, delta);
        AesEncrypt(tmp, pad);
        tmp.Clear();
        encrypted.Slice(pos, remaining).CopyTo(tmp);
        Xor(tmp, tmp, pad);
        Xor(checksum, checksum, tmp);
        tmp[..remaining].CopyTo(plain.Slice(pos, remaining));

        // 解出来的最后一块（除最后一字节）等于 delta 时可能是伪造的
        if (tmp[..^1].SequenceEqual(delta[..^1]))
            return false;

        S2Into(delta, pad);
        Xor(delta, delta, pad);
        Xor(tmp, delta, checksum);
        AesEncrypt(tmp, tag);
        return true;
    }
}
