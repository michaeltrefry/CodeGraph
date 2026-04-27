namespace CodeGraph.Data.MariaDb;

public interface IAesEncryptor
{
    string Encrypt(string plainText);
    string Decrypt(string encrypted);
}
