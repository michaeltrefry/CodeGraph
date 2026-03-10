namespace TC.CodeGraphApi.Data;

public class CodeGraphStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string MigrationsPath { get; set; } = "sql/migrations";
    public int BatchSize { get; set; } = 500;
}
