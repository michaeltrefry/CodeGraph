namespace CodeGraph.Services.Configuration;

public static class CodeGraphSettingsNormalizer
{
    public static void Normalize(CodeGraphServiceSettings settings)
    {
        Normalize(settings.RepositorySource);
    }

    public static void Normalize(RepositorySourceOptions settings)
    {
        settings.ReposCachePath = ExpandHomeDirectory(settings.ReposCachePath);
        settings.Folder.RootPath = ExpandHomeDirectory(settings.Folder.RootPath);
    }

    private static string ExpandHomeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (!path.StartsWith("~"))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
            return home;

        var remainder = path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(home, remainder);
    }
}
