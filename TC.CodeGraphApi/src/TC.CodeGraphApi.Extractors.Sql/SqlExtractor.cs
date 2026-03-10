using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.Sql;

public class SqlExtractor : ICodeExtractor
{
    private readonly ILogger<SqlExtractor> _logger;

    public SqlExtractor(ILogger<SqlExtractor> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string> { ".sql" };

    public Task<ExtractionResult> ExtractAsync(string filePath, string content,
        ExtractorContext context, CancellationToken ct = default)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);

        using var reader = new StringReader(content);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogWarning("SQL parse errors in {FilePath}: {ErrorCount} errors (first: {Error})",
                filePath, errors.Count, errors[0].Message);
            // Continue with best-effort extraction — ScriptDom still produces a partial AST
        }

        if (fragment == null)
        {
            _logger.LogWarning("Failed to parse SQL in {FilePath}", filePath);
            return Task.FromResult(new ExtractionResult());
        }

        var visitor = new SqlGraphVisitor(context, filePath);
        fragment.Accept(visitor);

        return Task.FromResult(visitor.GetResult());
    }
}
