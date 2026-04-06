using System.Data;
using System.Text.Json;
using Dapper;

namespace CodeGraph.Data;

public class JsonTypeHandler : SqlMapper.TypeHandler<Dictionary<string, object>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override void SetValue(IDbDataParameter parameter, Dictionary<string, object>? value)
    {
        parameter.Value = value is null || value.Count == 0
            ? DBNull.Value
            : JsonSerializer.Serialize(value, JsonOptions);
    }

    public override Dictionary<string, object> Parse(object value)
    {
        if (value is string json && !string.IsNullOrEmpty(json))
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions) ?? new();
        }
        return new();
    }
}
