namespace CodeGraph.Tests.Data;

internal static class MariaDbTestEnvironment
{
    public static string RequireConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("CODEGRAPH_MARIADB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "CODEGRAPH_MARIADB_TEST_CONNECTION is required for the MariaDB integration test suite.");
        }

        return connectionString;
    }
}
