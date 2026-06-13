using System.Net.Http;
using System.Security.Cryptography;
using M3U8Downloader.Models;
using Microsoft.Extensions.Logging;

namespace M3U8Downloader.Services;

public class AesDecryptionService
{
    private readonly ILogger<AesDecryptionService> _logger;
    private readonly Dictionary<string, byte[]> _keyCache = new();
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public AesDecryptionService(ILogger<AesDecryptionService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("DownloadClient");
    }

    public async Task<byte[]> DecryptSegmentAsync(
        byte[] encryptedData,
        EncryptionInfo encryption,
        int segmentSequenceNumber,
        CancellationToken ct = default)
    {
        if (!encryption.IsEncrypted)
            return encryptedData;

        var key = await GetOrCreateKeyAsync(encryption.KeyUri, ct);
        var iv = encryption.IV ?? CreateIVFromSequence(segmentSequenceNumber);

        return encryption.Method.ToUpperInvariant() switch
        {
            "AES-128" => DecryptAesCbc(encryptedData, key, iv, 16),
            "AES-256" => DecryptAesCbc(encryptedData, key, iv, 32),
            "AES-256-CTR" => DecryptAesCtr(encryptedData, key, iv),
            _ => throw new NotSupportedException($"Encryption method not supported: {encryption.Method}")
        };
    }

    private async Task<byte[]> GetOrCreateKeyAsync(string keyUri, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(keyUri))
            throw new InvalidOperationException("Key URI is empty");

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_keyCache.TryGetValue(keyUri, out var cachedKey))
                return cachedKey;

            _logger.LogInformation("Downloading encryption key from: {Uri}", keyUri);
            var keyData = await _httpClient.GetByteArrayAsync(keyUri, ct);
            _keyCache[keyUri] = keyData;
            return keyData;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static byte[] CreateIVFromSequence(int sequenceNumber)
    {
        // HLS spec: if IV is not specified, use the media sequence number as a 16-byte big-endian integer
        var iv = new byte[16];
        var seqBytes = BitConverter.GetBytes(sequenceNumber);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(seqBytes);
        Array.Copy(seqBytes, 0, iv, 12, 4); // Place in last 4 bytes (big-endian)
        return iv;
    }

    private static byte[] DecryptAesCbc(byte[] data, byte[] key, byte[] iv, int expectedKeySize)
    {
        // Ensure key is the expected size
        var actualKey = key;
        if (key.Length != expectedKeySize)
        {
            actualKey = new byte[expectedKeySize];
            Array.Copy(key, actualKey, Math.Min(key.Length, expectedKeySize));
        }

        using var aes = Aes.Create();
        aes.Key = actualKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] DecryptAesCtr(byte[] data, byte[] key, byte[] iv)
    {
        // AES-CTR mode: not natively supported in .NET, implement manually
        var actualKey = key;
        if (key.Length != 32)
        {
            actualKey = new byte[32];
            Array.Copy(key, actualKey, Math.Min(key.Length, 32));
        }

        using var aes = Aes.Create();
        aes.Key = actualKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var output = new byte[data.Length];
        var counter = (byte[])iv.Clone();
        var blockSize = 16;
        var encryptedBlock = new byte[blockSize];

        using var encryptor = aes.CreateEncryptor();

        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            encryptor.TransformBlock(counter, 0, blockSize, encryptedBlock, 0);

            var remaining = Math.Min(blockSize, data.Length - offset);
            for (int i = 0; i < remaining; i++)
                output[offset + i] = (byte)(data[offset + i] ^ encryptedBlock[i]);

            IncrementCounter(counter);
        }

        return output;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    public void ClearKeyCache()
    {
        _keyCache.Clear();
    }
}
