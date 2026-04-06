namespace CodeGraph.Services.Configuration;

public static class SettingsExtensions
{
    public static void LoadEnvironmentOverrides(this CodeGraphServiceSettings settings)
    {
        LoadDotEnv();

        Bind("CODEGRAPH_PROVIDER",            v => settings.StorageOptions.Provider = v);
        Bind("CODEGRAPH_MYSQL",              v => settings.StorageOptions.ConnectionString = v);
        Bind("CODEGRAPH_NEO4J_URI",          v => settings.StorageOptions.Neo4jUri = v);
        Bind("CODEGRAPH_NEO4J_USER",         v => settings.StorageOptions.Neo4jUsername = v);
        Bind("CODEGRAPH_NEO4J_PASSWORD",     v => settings.StorageOptions.Neo4jPassword = v);
        Bind("CODEGRAPH_NEO4J_DATABASE",     v => settings.StorageOptions.Neo4jDatabase = v);
        Bind("CODEGRAPH_EMBEDDING_MODEL",    v => settings.StorageOptions.EmbeddingModelPath = v);
        Bind("ANTHROPIC_API_KEY",            v => settings.AnalysisOptions.ApiKey = v);
        Bind("ANALYSIS_MODEL",              v => settings.AnalysisOptions.Model = v);
        Bind("CODEGRAPH_MAX_PARALLEL_ANALYSES", v => settings.IndexingOptions.MaxParallelFiles = int.Parse(v));
        Bind("CODEGRAPH_TS_PORT",           v => settings.TsPort = int.Parse(v));
        Bind("GITLAB_PRIVATETOKEN",         v => settings.GitLabOptions.PrivateToken = v);
        Bind("GITLAB_BASEURL",              v => settings.GitLabOptions.BaseUrl = v);
        Bind("GITLAB_REPOSCACHEPATH",       v => settings.GitLabOptions.ReposCachePath = v);
        Bind("GITLAB_EXCLUDEDGROUPS",       v => settings.GitLabOptions.ExcludedGroups = v.Split(",").ToList());
    }

    private static void Bind(string envVar, Action<string> setter)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(value))
            setter(value);
    }

    private static void LoadDotEnv()
    {
        var path = GetEnvFile();
        if (path == null) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string? GetEnvFile()
    {
        var dir = Directory.GetCurrentDirectory();
        var path = Path.Combine(dir, ".env");
        while (!File.Exists(path))
        {
            var parent = Directory.GetParent(dir);
            if (parent == null || !parent.Exists) break;
            dir = parent.FullName;
            path = Path.Combine(dir, ".env");
        }
        return !File.Exists(path) ? null : path;
    }
}
