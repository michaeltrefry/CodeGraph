using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Extractors;

/// <summary>
/// Reads the native targets of an Xcode project (<c>*.xcodeproj/project.pbxproj</c>, an OpenStep
/// property list) and the Swift module each one builds, so <c>import M</c> (and
/// <c>@testable import M</c>) of an in-repository app or framework target binds to that target
/// rather than to an external module stub.
/// </summary>
/// <remarks>
/// The module name follows Xcode's build settings: <c>PRODUCT_MODULE_NAME</c>, else
/// <c>PRODUCT_NAME</c> made a C99 extended identifier (Xcode's default
/// <c>$(PRODUCT_NAME:c99extidentifier)</c>), else the target name. Target settings override the
/// project's for the same configuration. <c>$(TARGET_NAME)</c>, <c>$(PRODUCT_NAME)</c>,
/// <c>$(PRODUCT_MODULE_NAME)</c> and <c>$(inherited)</c> references and the
/// <c>c99extidentifier</c>, <c>identifier</c>, <c>rfc1034identifier</c>, <c>lower</c> and
/// <c>upper</c> modifiers are evaluated; a setting that references anything else (an
/// <c>.xcconfig</c> or environment value) is not literal and falls through to the next rule.
/// When configurations disagree, <c>Release</c> wins, else the first configuration listed.
/// </remarks>
internal static partial class XcodeProjectReader
{
    public const string ProjectFileName = "project.pbxproj";

    /// <summary>The native targets of a <c>project.pbxproj</c>, in file order.</summary>
    /// <exception cref="FormatException">The file is not a readable OpenStep property list.</exception>
    public static IReadOnlyList<XcodeNativeTarget> ReadNativeTargets(string content)
    {
        if (new Parser(content).ParseDocument() is not Dictionary<string, object> root ||
            root.GetValueOrDefault("objects") is not Dictionary<string, object> objects)
            throw new FormatException("The Xcode project has no objects table.");

        var projectSettings = objects.Values.OfType<Dictionary<string, object>>()
            .Where(entry => Text(entry, "isa") == "PBXProject")
            .Select(project => ConfigurationSettings(objects, project))
            .FirstOrDefault() ?? [];

        var targets = new List<XcodeNativeTarget>();
        foreach (var target in objects.Values.OfType<Dictionary<string, object>>()
                     .Where(entry => Text(entry, "isa") == "PBXNativeTarget"))
        {
            var name = Text(target, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var targetSettings = ConfigurationSettings(objects, target);
            var configurationNames = targetSettings.Keys
                .Concat(projectSettings.Keys.Where(key => !targetSettings.ContainsKey(key)))
                .ToList();
            var moduleNames = configurationNames
                .Select(configuration => (Configuration: configuration, Module: ModuleName(
                    name,
                    Text(target, "productName"),
                    Merge(projectSettings.GetValueOrDefault(configuration), targetSettings.GetValueOrDefault(configuration)))))
                .ToList();
            var moduleName = moduleNames.Select(entry => entry.Module).Distinct(StringComparer.Ordinal).Count() > 1
                ? moduleNames.FirstOrDefault(entry => entry.Configuration == "Release").Module ?? moduleNames[0].Module
                : moduleNames.FirstOrDefault().Module ?? ModuleName(name, Text(target, "productName"), []);

            targets.Add(new XcodeNativeTarget(name, moduleName, Text(target, "productType")));
        }

        return targets;
    }

    /// <summary>Build settings of each configuration of <paramref name="owner"/>'s configuration list.</summary>
    private static Dictionary<string, Dictionary<string, object>> ConfigurationSettings(
        Dictionary<string, object> objects, Dictionary<string, object> owner)
    {
        var settings = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        if (Text(owner, "buildConfigurationList") is not { } listId ||
            objects.GetValueOrDefault(listId) is not Dictionary<string, object> list ||
            list.GetValueOrDefault("buildConfigurations") is not List<object> configurationIds)
            return settings;

        foreach (var id in configurationIds.OfType<string>())
        {
            if (objects.GetValueOrDefault(id) is Dictionary<string, object> configuration &&
                Text(configuration, "name") is { } configurationName &&
                !settings.ContainsKey(configurationName))
            {
                settings[configurationName] =
                    configuration.GetValueOrDefault("buildSettings") as Dictionary<string, object> ?? [];
            }
        }

        return settings;
    }

    private static Dictionary<string, object> Merge(
        Dictionary<string, object>? project, Dictionary<string, object>? target)
    {
        var merged = new Dictionary<string, object>(project ?? [], StringComparer.Ordinal);
        foreach (var (key, value) in target ?? [])
            merged[key] = value;
        return merged;
    }

    private static string ModuleName(string targetName, string? productName, Dictionary<string, object> settings)
    {
        var evaluator = new SettingEvaluator(targetName, productName, settings);
        if (evaluator.Evaluate("PRODUCT_MODULE_NAME") is { Length: > 0 } module)
            return module;
        if (evaluator.ProductName() is { Length: > 0 } product)
            return C99ExtendedIdentifier(product);
        return C99ExtendedIdentifier(targetName);
    }

    /// <summary>
    /// Xcode's <c>c99extidentifier</c>: every character that cannot appear in a C99 extended
    /// identifier becomes <c>_</c>, and a leading digit is prefixed with <c>_</c>.
    /// </summary>
    internal static string C99ExtendedIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length + 1);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        if (builder.Length > 0 && char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }

    private static string? Text(Dictionary<string, object> dictionary, string key) =>
        dictionary.GetValueOrDefault(key) as string;

    /// <summary>Evaluates build-setting references Xcode resolves without an external source.</summary>
    private sealed partial class SettingEvaluator(
        string targetName, string? productName, Dictionary<string, object> settings)
    {
        private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

        /// <summary>The literal value of <paramref name="setting"/>; null when unset or not literal.</summary>
        public string? Evaluate(string setting)
        {
            if (settings.GetValueOrDefault(setting) is not string raw || !_evaluating.Add(setting))
                return null;
            try
            {
                return Expand(raw);
            }
            finally
            {
                _evaluating.Remove(setting);
            }
        }

        private string? Expand(string raw)
        {
            var failed = false;
            var expanded = Reference().Replace(raw, match =>
            {
                var name = match.Groups["name"].Value;
                var value = name switch
                {
                    "inherited" => "",
                    "TARGET_NAME" => targetName,
                    "PRODUCT_NAME" => ProductName(),
                    "PRODUCT_MODULE_NAME" => Evaluate(name),
                    _ => null
                };
                if (value is null)
                {
                    failed = true;
                    return "";
                }

                foreach (var modifier in match.Groups["modifier"].Captures.Select(capture => capture.Value))
                {
                    switch (modifier)
                    {
                        case "c99extidentifier" or "identifier":
                            value = C99ExtendedIdentifier(value);
                            break;
                        case "rfc1034identifier":
                            value = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '-').ToArray());
                            break;
                        case "lower":
                            value = value.ToLowerInvariant();
                            break;
                        case "upper":
                            value = value.ToUpperInvariant();
                            break;
                        default:
                            failed = true;
                            break;
                    }
                }

                return value;
            });
            expanded = expanded.Trim();
            return failed || expanded.Length == 0 || expanded.Contains('$') ? null : expanded;
        }

        /// <summary>
        /// The product name: the literal <c>PRODUCT_NAME</c> setting, or when it is unset the
        /// target's product name (Xcode names the product after the target). Null when the setting
        /// is present but not literal.
        /// </summary>
        public string? ProductName() =>
            settings.ContainsKey("PRODUCT_NAME")
                ? Evaluate("PRODUCT_NAME")
                : NonEmpty(productName) ?? targetName;

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        // $(NAME), ${NAME}, $(NAME:mod1:mod2)
        [GeneratedRegex(@"\$[\(\{](?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<modifier>[A-Za-z0-9_]+))*[\)\}]")]
        private static partial Regex Reference();
    }

    /// <summary>
    /// OpenStep (ASCII) property list: dictionaries <c>{ key = value; }</c>, arrays
    /// <c>( a, b, )</c>, quoted and unquoted strings, <c>&lt;hex&gt;</c> data, and C/C++ comments.
    /// </summary>
    private sealed class Parser(string text)
    {
        private int _position;

        public object ParseDocument()
        {
            var value = ParseValue();
            SkipTrivia();
            if (_position < text.Length)
                throw Error("Unexpected content after the root value");
            return value;
        }

        private object ParseValue()
        {
            SkipTrivia();
            if (_position >= text.Length)
                throw Error("Unexpected end of file");

            return text[_position] switch
            {
                '{' => ParseDictionary(),
                '(' => ParseArray(),
                '"' or '\'' => ParseQuoted(),
                '<' => ParseData(),
                _ => ParseUnquoted()
            };
        }

        private Dictionary<string, object> ParseDictionary()
        {
            _position++;
            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            while (true)
            {
                SkipTrivia();
                if (Peek() == '}')
                {
                    _position++;
                    return dictionary;
                }

                if (ParseValue() is not string key)
                    throw Error("Dictionary key is not a string");
                Expect('=');
                var value = ParseValue();
                Expect(';');
                dictionary[key] = value;
            }
        }

        private List<object> ParseArray()
        {
            _position++;
            var array = new List<object>();
            while (true)
            {
                SkipTrivia();
                if (Peek() == ')')
                {
                    _position++;
                    return array;
                }

                array.Add(ParseValue());
                SkipTrivia();
                if (Peek() == ',')
                    _position++;
                else if (Peek() != ')')
                    throw Error("Expected ',' or ')'");
            }
        }

        private string ParseQuoted()
        {
            var quote = text[_position++];
            var builder = new StringBuilder();
            while (_position < text.Length && text[_position] != quote)
            {
                var character = text[_position++];
                if (character != '\\' || _position >= text.Length)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = text[_position++];
                switch (escaped)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'U' when _position + 4 <= text.Length &&
                                  int.TryParse(text.AsSpan(_position, 4), System.Globalization.NumberStyles.HexNumber,
                                      System.Globalization.CultureInfo.InvariantCulture, out var code):
                        builder.Append((char)code);
                        _position += 4;
                        break;
                    default: builder.Append(escaped); break;
                }
            }

            if (_position >= text.Length)
                throw Error("Unterminated string");
            _position++;
            return builder.ToString();
        }

        private string ParseData()
        {
            var end = text.IndexOf('>', _position);
            if (end < 0)
                throw Error("Unterminated data");
            var data = text[(_position + 1)..end];
            _position = end + 1;
            return data;
        }

        private string ParseUnquoted()
        {
            var start = _position;
            while (_position < text.Length && IsUnquotedCharacter(text[_position]))
                _position++;
            if (_position == start)
                throw Error($"Unexpected character '{text[_position]}'");
            return text[start.._position];
        }

        private static bool IsUnquotedCharacter(char character) =>
            char.IsLetterOrDigit(character) || character is '_' or '$' or '/' or ':' or '.' or '-' or '+' or '~';

        private void Expect(char expected)
        {
            SkipTrivia();
            if (Peek() != expected)
                throw Error($"Expected '{expected}'");
            _position++;
        }

        private char? Peek() => _position < text.Length ? text[_position] : null;

        private void SkipTrivia()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                }
                else if (text.AsSpan(_position).StartsWith("//"))
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end + 1;
                }
                else if (text.AsSpan(_position).StartsWith("/*"))
                {
                    var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    _position = end < 0 ? text.Length : end + 2;
                }
                else
                {
                    return;
                }
            }
        }

        private FormatException Error(string message) =>
            new($"{message} at offset {_position} of the Xcode project.");
    }
}

/// <summary>A <c>PBXNativeTarget</c> and the Swift module it builds.</summary>
internal sealed record XcodeNativeTarget(string Name, string ModuleName, string? ProductType);
