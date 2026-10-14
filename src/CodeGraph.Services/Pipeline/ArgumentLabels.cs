using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Argument-label signatures, used to pick among same-named overloads for languages whose
/// argument labels are part of a callable's identity (Swift: <c>init(radius:)</c> vs <c>init()</c>).
/// <para>
/// A signature is a sequence of <c>label:</c> segments, empty for no arguments; <c>_</c> is an
/// unlabeled argument. On a declaration (the <see cref="Property"/> node property) a segment may
/// carry <c>=</c> (the parameter has a default value and may be omitted) and/or <c>...</c>
/// (variadic: zero or more arguments, unlabeled after the first): <c>x=:_...:to:</c>. On a call
/// (<see cref="UnresolvedCall.ArgumentLabels"/>) a segment may be <c>*</c>, an unlabeled trailing
/// closure, which can bind to a parameter of any label.
/// </para>
/// </summary>
public static class ArgumentLabels
{
    /// <summary>Node property holding a declaration's argument-label signature.</summary>
    public const string Property = "argument_labels";

    /// <summary>A call-side segment matching a parameter of any label.</summary>
    public const string AnyLabel = "*";

    /// <summary>
    /// The candidates whose declared signature accepts <paramref name="callLabels"/>. A candidate
    /// without a declared signature (another language, a stub) cannot be ruled out and is kept.
    /// </summary>
    public static IReadOnlyList<GraphNode> Select(IReadOnlyList<GraphNode> candidates, string callLabels) =>
        candidates
            .Where(candidate =>
                !candidate.Properties.TryGetValue(Property, out var declared)
                || declared?.ToString() is not { } signature
                || Accepts(signature, callLabels))
            .ToList();

    /// <summary>Whether a declaration with <paramref name="declared"/> accepts a call with <paramref name="call"/>.</summary>
    public static bool Accepts(string declared, string call)
    {
        var parameters = Split(declared).Select(Parameter.Parse).ToList();
        var arguments = Split(call);
        return Matches(parameters, 0, arguments, 0);
    }

    /// <summary>
    /// Whether <paramref name="arguments"/> from <paramref name="argument"/> on bind to
    /// <paramref name="parameters"/> from <paramref name="parameter"/> on, in order. A required
    /// parameter takes exactly one argument; a defaulted one takes one or none; a variadic one
    /// takes none, or one followed by any number of unlabeled arguments. Every choice is tried,
    /// so a trailing closure (<c>*</c>) reaches a later parameter past defaulted or variadic ones.
    /// </summary>
    private static bool Matches(List<Parameter> parameters, int parameter, List<string> arguments, int argument)
    {
        if (parameter == parameters.Count)
            return argument == arguments.Count;

        var current = parameters[parameter];
        if ((current.HasDefault || current.Variadic)
            && Matches(parameters, parameter + 1, arguments, argument))
        {
            return true;
        }

        if (argument >= arguments.Count || !LabelMatches(current.Label, arguments[argument]))
            return false;

        if (Matches(parameters, parameter + 1, arguments, argument + 1))
            return true;

        if (!current.Variadic)
            return false;

        for (var next = argument + 1; next < arguments.Count && arguments[next] == "_"; next++)
        {
            if (Matches(parameters, parameter + 1, arguments, next + 1))
                return true;
        }

        return false;
    }

    private static bool LabelMatches(string parameterLabel, string argumentLabel) =>
        argumentLabel == AnyLabel || string.Equals(parameterLabel, argumentLabel, StringComparison.Ordinal);

    private static List<string> Split(string signature) =>
        signature.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private sealed record Parameter(string Label, bool HasDefault, bool Variadic)
    {
        public static Parameter Parse(string segment)
        {
            var variadic = segment.EndsWith("...", StringComparison.Ordinal);
            if (variadic)
                segment = segment[..^3];
            var hasDefault = segment.EndsWith('=');
            if (hasDefault)
                segment = segment[..^1];
            return new Parameter(segment, hasDefault, variadic);
        }
    }
}
