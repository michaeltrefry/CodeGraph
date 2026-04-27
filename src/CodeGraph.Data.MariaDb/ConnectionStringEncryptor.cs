using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CodeGraph.Data.MariaDb;

public class ConnectionStringEncryptor(IOptions<MariaDbStorageOptions> optionsAccessor) : IAesEncryptor
{
    private const string GcmPrefix = "aes-gcm:v1:";
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    public string Encrypt(string plainText)
    {
        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, nonce.Length + tag.Length, cipherBytes.Length);

        return GcmPrefix + Convert.ToBase64String(result);
    }

    public string Decrypt(string encrypted)
    {
        var key = GetKey();
        if (!encrypted.StartsWith(GcmPrefix, StringComparison.Ordinal))
        {
            return DecryptLegacyCbc(encrypted, key);
        }

        var fullBytes = Convert.FromBase64String(encrypted[GcmPrefix.Length..]);
        if (fullBytes.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Encrypted payload is too short.");
        }

        var nonce = fullBytes[..NonceSizeBytes];
        var tag = fullBytes[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
        var cipherBytes = fullBytes[(NonceSizeBytes + TagSizeBytes)..];
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string DecryptLegacyCbc(string encrypted, byte[] key)
    {
        var fullBytes = Convert.FromBase64String(encrypted);
        using var aes = Aes.Create();
        aes.Key = key;

        var iv = new byte[aes.BlockSize / 8];
        var cipherBytes = new byte[fullBytes.Length - iv.Length];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullBytes, iv.Length, cipherBytes, 0, cipherBytes.Length);

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(options.EncryptionKey))
        {
            throw new InvalidOperationException(
                "EncryptionKey is not configured in MariaDbStorageOptions. Cannot encrypt/decrypt database source connection strings.");
        }

        var key = Convert.FromBase64String(options.EncryptionKey);
        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                "EncryptionKey must be a base64-encoded 32-byte AES-256 key.");
        }

        return key;
    }
}
