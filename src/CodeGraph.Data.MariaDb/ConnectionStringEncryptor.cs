using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CodeGraph.Data.MariaDb;

public class ConnectionStringEncryptor(IOptions<MariaDbStorageOptions> optionsAccessor)
{
    private readonly MariaDbStorageOptions options = optionsAccessor.Value;

    public string Encrypt(string plainText)
    {
        var key = GetKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string encrypted)
    {
        var key = GetKey();
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

        return Convert.FromBase64String(options.EncryptionKey);
    }
}
