using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.Sql;

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
        content = NormalizeMySqlSyntax(content);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);

        using var reader = new StringReader(content);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogDebug("SQL parse errors in {FilePath}: {ErrorCount} errors (first: {Error})",
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

    /// <summary>
    /// Convert MySQL-specific syntax to something T-SQL ScriptDom can parse.
    /// ScriptDom will still report errors for unhandled MySQL-isms, but these
    /// normalizations let it produce a usable partial AST for most files.
    /// </summary>
    private static string NormalizeMySqlSyntax(string sql)
    {
        // Replace # line comments with -- (MySQL uses # as a comment prefix).
        // Exclude #TempTable and #variable references (T-SQL temp tables).
        sql = Regex.Replace(sql, @"(?<=^|\s)#(?![A-Za-z_])(.*)$", "-- $1", RegexOptions.Multiline);

        // Replace backtick identifiers with square brackets: `name` -> [name]
        sql = Regex.Replace(sql, @"`([^`]*)`", "[$1]");

        // Strip DELIMITER statements and the custom delimiter tokens they introduce.
        sql = Regex.Replace(sql, @"^\s*DELIMITER\s+\S+\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\$\$|//\s*$", ";", RegexOptions.Multiline);

        // Strip MySQL engine/charset clauses from CREATE TABLE
        sql = Regex.Replace(sql, @"\)\s*ENGINE\s*=\s*\w+[^;]*;", ");", RegexOptions.IgnoreCase);

        // Strip IF NOT EXISTS (T-SQL uses a different pattern)
        sql = Regex.Replace(sql, @"\bIF\s+NOT\s+EXISTS\b", "", RegexOptions.IgnoreCase);

        // AUTO_INCREMENT -> IDENTITY (close enough for structural parsing)
        sql = Regex.Replace(sql, @"\bAUTO_INCREMENT\b", "IDENTITY(1,1)", RegexOptions.IgnoreCase);

        // Strip UNSIGNED (no T-SQL equivalent, not structurally important)
        sql = Regex.Replace(sql, @"\bUNSIGNED\b", "", RegexOptions.IgnoreCase);

        // TINYINT(1) / INT(11) etc. -> strip the display width
        sql = Regex.Replace(sql, @"\b(TINYINT|SMALLINT|MEDIUMINT|INT|BIGINT)\(\d+\)", "$1", RegexOptions.IgnoreCase);

        // BOOL/BOOLEAN -> BIT
        sql = Regex.Replace(sql, @"\bBOOL(?:EAN)?\b", "BIT", RegexOptions.IgnoreCase);

        // ENUM(...) -> VARCHAR(255) (structural placeholder)
        sql = Regex.Replace(sql, @"\bENUM\s*\([^)]*\)", "VARCHAR(255)", RegexOptions.IgnoreCase);

        // TEXT variants MySQL uses that T-SQL doesn't have
        sql = Regex.Replace(sql, @"\bLONGTEXT\b", "NVARCHAR(MAX)", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bMEDIUMTEXT\b", "NVARCHAR(MAX)", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bTINYTEXT\b", "NVARCHAR(255)", RegexOptions.IgnoreCase);

        // LONGBLOB/MEDIUMBLOB/TINYBLOB -> VARBINARY(MAX)
        sql = Regex.Replace(sql, @"\b(?:LONG|MEDIUM|TINY)?BLOB\b", "VARBINARY(MAX)", RegexOptions.IgnoreCase);

        // DOUBLE -> FLOAT
        sql = Regex.Replace(sql, @"\bDOUBLE\b", "FLOAT", RegexOptions.IgnoreCase);

        // Strip ON UPDATE CURRENT_TIMESTAMP
        sql = Regex.Replace(sql, @"\bON\s+UPDATE\s+CURRENT_TIMESTAMP\b", "", RegexOptions.IgnoreCase);

        // DEFAULT CURRENT_TIMESTAMP -> DEFAULT GETDATE()
        sql = Regex.Replace(sql, @"\bDEFAULT\s+CURRENT_TIMESTAMP\b", "DEFAULT GETDATE()", RegexOptions.IgnoreCase);

        // Strip CHARACTER SET / COLLATE clauses
        sql = Regex.Replace(sql, @"\b(DEFAULT\s+)?(CHARACTER\s+SET|CHARSET)\s*=?\s*\w+", "", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bCOLLATE\s*=?\s*\w+", "", RegexOptions.IgnoreCase);

        // Strip AUTO_INCREMENT=N table option (already handled column-level above)
        sql = Regex.Replace(sql, @"\bAUTO_INCREMENT\s*=\s*\d+", "", RegexOptions.IgnoreCase);

        return sql;
    }
}
