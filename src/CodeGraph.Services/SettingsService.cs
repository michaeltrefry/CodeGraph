using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Data;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services;

public class SettingsService(
    CodeGraphServiceSettings settings,
    IAdminStore store) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<string> GetEffectiveSettingsJsonAsync()
    {
        var baseJson = JsonSerializer.Serialize(settings, JsonOptions);
        var baseNode = JsonNode.Parse(baseJson)!.AsObject();

        // Merge DB overrides on top
        var overrideEntity = await store.GetLatestSettingsOverrideAsync();
        if (overrideEntity is not null)
        {
            var overrideNode = JsonNode.Parse(overrideEntity.SettingsJson);
            if (overrideNode is JsonObject overrideObj)
            {
                MergeJson(baseNode, overrideObj);
            }
        }

        // Remove connectionString from storage options to avoid exposing secrets
        if (baseNode["storageOptions"] is JsonObject storageObj)
        {
            storageObj.Remove("connectionString");
        }

        return baseNode.ToJsonString(JsonOptions);
    }

    public async Task UpdateOverridesAsync(string settingsJson, string username)
    {
        // Validate the JSON is parseable
        JsonNode.Parse(settingsJson);

        await store.UpsertSettingsOverrideAsync(new SettingsOverrideEntity
        {
            SettingsJson = settingsJson,
            UpdatedBy = username,
            UpdatedAt = DateTime.UtcNow
        });
    }

    private static void MergeJson(JsonObject target, JsonObject source)
    {
        foreach (var prop in source)
        {
            if (prop.Value is JsonObject sourceChild && target[prop.Key] is JsonObject targetChild)
            {
                MergeJson(targetChild, sourceChild);
            }
            else
            {
                target[prop.Key] = prop.Value?.DeepClone();
            }
        }
    }
}
