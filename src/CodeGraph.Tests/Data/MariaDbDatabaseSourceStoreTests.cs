using CodeGraph.Data;
using CodeGraph.Data.MariaDb;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Shouldly;
using System.Security.Cryptography;
using System.Text;

namespace CodeGraph.Tests.Data;

public class MariaDbDatabaseSourceStoreTests
{
    [Fact]
    public void MySqlDatabaseSourceStore_ImplementsStandaloneDatabaseSourceContract()
    {
        typeof(IDatabaseSourceStore).IsAssignableFrom(typeof(MySqlDatabaseSourceStore)).ShouldBeTrue();
    }

    [Fact]
    public async Task MySqlDatabaseSourceStore_RoundTripsEncryptedSourcesWhenConnectionIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(connectionString);
        var databaseName = $"codegraph_db_source_store_test_{Guid.NewGuid():N}";
        builder.Database = databaseName;

        var options = new MariaDbStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            MigrationsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../sql/migrations"),
            EncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
        };

        var runner = new MariaDbMigrationRunner(
            Options.Create(options),
            NullLogger<MariaDbMigrationRunner>.Instance);

        try
        {
            await runner.ApplyConfiguredMigrationsAsync();

            await using var context = new CodeGraphDbContext(CreateOptions(builder.ConnectionString));
            var store = new MySqlDatabaseSourceStore(context, new ConnectionStringEncryptor(Options.Create(options)));
            const string plainConnectionString = "Server=db;Database=app;User ID=app;Password=secret";

            var created = await store.CreateAsync(new DatabaseSourceEntity
            {
                ServerName = "db",
                DatabaseName = "app",
                ConnectionString = plainConnectionString,
                Enabled = true
            });

            created.Id.ShouldBeGreaterThan(0);
            created.ConnectionString.ShouldBe(plainConnectionString);

            var storedCipherText = (await context.DatabaseSources.AsNoTracking().SingleAsync()).ConnectionString;
            storedCipherText.ShouldNotBe(plainConnectionString);
            storedCipherText.ShouldStartWith("aes-gcm:v1:");

            (await store.GetAsync(created.Id))!.ConnectionString.ShouldBe(plainConnectionString);
            (await store.ListAsync()).Single().ConnectionString.ShouldBe(plainConnectionString);

            var updated = await store.UpdateAsync(
                created.Id,
                serverName: "db2",
                databaseName: null,
                connectionString: "Server=db2;Database=app;User ID=app;Password=secret2",
                enabled: false);

            updated.ShouldNotBeNull();
            updated.ServerName.ShouldBe("db2");
            updated.Enabled.ShouldBeFalse();
            updated.ConnectionString.ShouldContain("db2");

            await store.UpdateLastSyncedAsync(created.Id);
            (await store.GetAsync(created.Id))!.LastSyncedAt.ShouldNotBeNull();

            (await store.DeleteAsync(created.Id)).ShouldBeTrue();
            (await store.GetAsync(created.Id)).ShouldBeNull();
        }
        finally
        {
            await DropDatabaseAsync(builder.ConnectionString, databaseName);
        }
    }

    [Fact]
    public void ConnectionStringEncryptor_RoundTripsGcmAndRejectsTampering()
    {
        var encryptor = CreateEncryptor(out _);
        var encrypted = encryptor.Encrypt("Server=db;Password=secret");

        encrypted.ShouldStartWith("aes-gcm:v1:");
        encryptor.Decrypt(encrypted).ShouldBe("Server=db;Password=secret");

        var tamperedBytes = Convert.FromBase64String(encrypted["aes-gcm:v1:".Length..]);
        tamperedBytes[^1] ^= 0x7F;
        var tampered = "aes-gcm:v1:" + Convert.ToBase64String(tamperedBytes);

        Should.Throw<CryptographicException>(() => encryptor.Decrypt(tampered));
    }

    [Fact]
    public void ConnectionStringEncryptor_DecryptsLegacyCbcCipherText()
    {
        var encryptor = CreateEncryptor(out var key);
        var legacy = EncryptLegacyCbc("Server=legacy;Password=old", key);

        legacy.ShouldNotStartWith("aes-gcm:v1:");
        encryptor.Decrypt(legacy).ShouldBe("Server=legacy;Password=old");
    }

    private static DbContextOptions<CodeGraphDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<CodeGraphDbContext>()
            .UseMySql(
                connectionString,
                ServerVersion.Create(new Version(11, 4, 0), ServerType.MariaDb))
            .Options;

    private static ConnectionStringEncryptor CreateEncryptor(out byte[] key)
    {
        key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        return new ConnectionStringEncryptor(Options.Create(new MariaDbStorageOptions
        {
            EncryptionKey = Convert.ToBase64String(key)
        }));
    }

    private static string EncryptLegacyCbc(string plainText, byte[] key)
    {
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

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = ""
        };

        await using var conn = new MySqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS `{databaseName}`");
    }
}
