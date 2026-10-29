using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CodeGraph.Models.Work;

/// <summary>
/// The one JSON contract of the work tools, shared by MCP and REST so both refuse the same calls
/// (E1): snake_case names, string enums only, and any member a type does not declare refused at
/// every level of the arguments (a misspelt <c>expected_versoin</c> must not turn a guarded write
/// into an unguarded one). The tool schemas are generated from the same types and options.
/// </summary>
public static class WorkJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The wire name of an enum value (<c>in_progress</c>).</summary>
    public static string Name<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static string Names<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetValues<TEnum>().Select(Name));

    /// <summary>
    /// Reads the arguments of <paramref name="tool"/> as <typeparamref name="T"/>, or throws
    /// <c>invalid_argument</c> naming what is wrong and the arguments that are valid there.
    /// </summary>
    public static T Parse<T>(JsonElement arguments, string tool) where T : class
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            arguments = JsonDocument.Parse("{}").RootElement;

        if (arguments.ValueKind != JsonValueKind.Object)
            throw WorkException.Invalid($"The arguments of {tool} must be a JSON object. Its arguments are: {MemberList(typeof(T))}.");

        try
        {
            return arguments.Deserialize<T>(Options)
                ?? throw WorkException.Invalid($"The arguments of {tool} must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw WorkException.Invalid(Describe(exception, typeof(T), tool));
        }
    }

    // A message for a refused argument: the path, what is wrong, and the valid names at that level.
    private static string Describe(JsonException exception, Type root, string tool)
    {
        var path = exception.Path ?? "$";
        var message = exception.Message;
        var (owner, leaf) = Resolve(root, path);

        const string unmapped = "could not be mapped to any .NET member";
        if (message.Contains(unmapped, StringComparison.Ordinal))
        {
            var name = UnmappedName(message) ?? leaf;
            var where = WhereOf(path, name);
            return $"{tool} has no argument '{where}'. Valid arguments{(where.Contains('.') ? " there" : string.Empty)}: {MemberList(owner)}. Names are compared exactly. Nothing was changed.";
        }

        if (message.Contains("missing required properties", StringComparison.Ordinal))
        {
            var target = path == "$" ? root : TypeAt(root, path) ?? owner;
            var missing = RequiredMembers(target);
            var location = path == "$" ? string.Empty : $" in '{Display(path)}'";
            return $"{tool} is missing a required argument{location}. Required: {string.Join(", ", missing)}. Valid arguments: {MemberList(target)}. Nothing was changed.";
        }

        var display = Display(path);
        var expected = leaf is not null ? ExpectedOf(owner, leaf) : null;
        return $"Argument '{display}' of {tool} is not valid{(expected is null ? string.Empty : $": expected {expected}")}. Nothing was changed.";
    }

    private static string? UnmappedName(string message)
    {
        var start = message.IndexOf('\'');
        var end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
        return start >= 0 && end > start ? message[(start + 1)..end] : null;
    }

    // "$.plan.items[0].titel" -> "plan.items[0].titel".
    private static string Display(string path) => path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');

    private static string WhereOf(string path, string? name)
    {
        var display = Display(path);
        if (name is null)
            return display;

        // The exception's path may or may not end with the unknown member itself.
        if (display == name || display.EndsWith("." + name, StringComparison.Ordinal))
            return display;

        return display.Length == 0 ? name : $"{display}.{name}";
    }

    // The type that owns the last segment of the path, and that segment's name.
    private static (Type Owner, string? Leaf) Resolve(Type root, string path)
    {
        var segments = Segments(path);
        var owner = root;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            owner = Step(owner, segments[index]) ?? owner;
        }

        return (owner, segments.Count > 0 ? segments[^1] : null);
    }

    private static Type? TypeAt(Type root, string path)
    {
        Type? current = root;
        foreach (var segment in Segments(path))
        {
            if (current is null)
                return null;
            current = Step(current, segment);
        }

        return current;
    }

    // Walks one property (and through a list to its elements).
    private static Type? Step(Type owner, string segment)
    {
        var info = Options.GetTypeInfo(owner);
        var property = info.Properties.FirstOrDefault(candidate => candidate.Name == segment);
        if (property is null)
            return null;

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return ElementType(type) ?? type;
    }

    private static Type? ElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    private static List<string> Segments(string path)
    {
        var segments = new List<string>();
        foreach (var part in Display(path).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[');
            var name = bracket >= 0 ? part[..bracket] : part;
            if (name.Length > 0)
                segments.Add(name.Trim('\'', '[', ']'));
        }

        return segments;
    }

    public static string MemberList(Type type)
    {
        var info = Options.GetTypeInfo(type);
        return info.Kind == JsonTypeInfoKind.Object
            ? string.Join(", ", info.Properties.Where(property => property.Set is not null).Select(property => property.Name))
            : "(none)";
    }

    private static IEnumerable<string> RequiredMembers(Type type) =>
        Options.GetTypeInfo(type).Properties.Where(property => property.IsRequired).Select(property => property.Name);

    private static string? ExpectedOf(Type owner, string member)
    {
        var property = Options.GetTypeInfo(owner).Properties.FirstOrDefault(candidate => candidate.Name == member);
        if (property is null)
            return null;

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type.IsEnum)
            return $"one of {string.Join(", ", Enum.GetNames(type).Select(JsonNamingPolicy.SnakeCaseLower.ConvertName))}";
        if (type == typeof(string))
            return "a string";
        if (type == typeof(bool))
            return "true or false";
        if (type == typeof(int) || type == typeof(long))
            return "an integer";
        if (ElementType(type) is { } element)
            return element == typeof(string) ? "a list of strings" : "a list of objects";
        return "an object";
    }

    /// <summary>
    /// The JSON schema of a tool's arguments: generated from the argument type with the same
    /// options that read the arguments, so every object carries <c>additionalProperties: false</c>
    /// and every enum its values. Descriptions come from <see cref="DescriptionAttribute"/>.
    /// </summary>
    public static JsonElement SchemaOf(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var description = context.PropertyInfo?.AttributeProvider?
                    .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                    .OfType<DescriptionAttribute>()
                    .FirstOrDefault()?.Description;
                if (description is not null && node is JsonObject schema)
                    schema.Insert(0, "description", description);
                return node;
            }
        };

        var node = Options.GetJsonSchemaAsNode(type, exporterOptions);
        return JsonSerializer.SerializeToElement(node);
    }
}
