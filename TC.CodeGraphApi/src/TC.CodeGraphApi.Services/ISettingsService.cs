namespace TC.CodeGraphApi.Services;

public interface ISettingsService
{
    /// <summary>
    /// Returns the effective settings JSON (Consul base merged with DB overrides).
    /// ConnectionString is excluded.
    /// </summary>
    Task<string> GetEffectiveSettingsJsonAsync();

    /// <summary>
    /// Saves settings overrides to the database.
    /// </summary>
    Task UpdateOverridesAsync(string settingsJson, string username);
}
