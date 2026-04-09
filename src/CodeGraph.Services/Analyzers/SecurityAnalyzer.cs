using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Extractors;

namespace CodeGraph.Services.Analyzers;

public class SecurityAnalyzer(
    IGraphStore store,
    IHttpClientFactory httpClientFactory,
    IFileSystem fileSystem,
    INuGetReferenceExtractor nugetReferenceExtractor,
    ISourceFileProvider sourceFileProvider,
    ILogger<SecurityAnalyzer> logger) : ISecurityAnalyzer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    // ── Secret detection patterns ───────────────────────────────────────

    // Last bool = whether entropy/structure analysis is required for non-config files.
    // Format-specific patterns (AWS key, PEM header) are already high-signal and skip entropy.
    private static readonly (string Name, Regex Pattern, string Severity, string? ExtFilter, bool EntropyCheck)[] SecretPatterns =
    [
        ("AWS Access Key", new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled, RegexTimeout),
            "critical", null, false),

        ("Service API Token", new Regex(@"(?:gh[pousr]_[A-Za-z0-9]{20,255}|sk-(?:proj-)?[A-Za-z0-9_\-]{20,255}|AIza[0-9A-Za-z\-_]{35})", RegexOptions.Compiled, RegexTimeout),
            "critical", null, false),

        ("Private Key", new Regex(@"-----BEGIN\s+(RSA|EC|DSA|OPENSSH)\s+PRIVATE\s+KEY-----", RegexOptions.Compiled, RegexTimeout),
            "critical", null, false),

        ("Connection String Password",
            new Regex(@"(?:password|pwd)[""']?\s*[:=]\s*[""']?([^;""'\s]{3,})", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "high", null, true),

        ("Credentialed URL",
            new Regex(@"https?://[^/\s:@""']+:([^@\s/""']{3,})@[^/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "high", null, false),

        ("Generic API Key",
            new Regex(@"[""']?(?:api[_\-]?key|apikey|secret[_\-]?key)[""']?\s*[:=]\s*[""']([A-Za-z0-9_:/+\-=]{20,})[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "medium", null, true),

        ("Config Secret",
            new Regex(@"[""']?(?:client[_\-]?secret|access[_\-]?token|refresh[_\-]?token|auth[_\-]?token|secret[_\-]?access[_\-]?key)[""']?\s*[:=]\s*[""']([^""']{10,})[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "high", null, true),

        ("JWT/Signing Secret",
            new Regex(@"[""']?(?:jwt[_\-]?secret|signing[_\-]?key)[""']?\s*[:=]\s*[""']([^""']{10,})[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "high", null, true),

        ("Hardcoded Password Assignment",
            new Regex(@"(?:password|passwd)[""']?\s*[:=]\s*""([^""]{3,})""", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
            "high", ".cs", true),
    ];

    // Placeholder values that aren't real secrets
    private static readonly Regex PlaceholderPattern = new(
        @"(?:changeme|todo|placeholder|xxx|your[_\-]?key|replace[_\-]?me|dummy|fake)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // False positive patterns — assignments to variables, property access, config lookups, empty/short values
    private static readonly Regex FalsePositivePattern = new(
        @"(?:" +
            // Empty or whitespace-only quoted values: password = "" or password = ''
            @"(?:password|passwd|pwd|pass)\s*=\s*[""']\s*[""']" +
            // Default parameter values in method signatures: string password = "..."
            @"|(?:string|String)\s+(?:password|passwd|pass|pwd)\s*=\s*[""']" +
            // Variable/property assignment: password = variable, password = model.Password
            @"|(?:password|passwd|pwd)\s*=\s*(?:\w+\.\w+|(?:this|model|request|options|config|settings|dto|entity|user|input|args|param|context|result|response|data|value|item|record|form|command|query)\.\w+)" +
            // Variable declaration (non-literal): string password = GetPwd(), var password = input
            // Requires non-quote char after = so string literals pass through to entropy analysis
            @"|(?:string|var|object)\s+(?:password|passwd|pass)\s*=\s*[^\s""']" +
            // Method parameter (no default): (string password), string password,
            @"|(?:string|String)\s+(?:password|passwd|pass|pwd)\s*[,)]" +
            // Interface/method signature context: lines containing method declarations with password params
            @"|(?:string|void|bool|Task|int)\s+\w+\s*\([^)]*(?:password|passwd|pass|pwd)\s*=" +
            // Config/env lookups: GetValue("password"), Configuration["password"]
            @"|(?:GetValue|GetSection|GetConnectionString)\s*\(\s*[""'](?:password|pwd)" +
            // Indexer access: ["password"], ["pwd"]
            @"|\[\s*[""'](?:password|pwd)[""']\s*\]" +
            // Null/empty checks: string.IsNullOrEmpty(password), password == null
            @"|(?:IsNullOrEmpty|IsNullOrWhiteSpace)\s*\(\s*(?:password|passwd|pwd)" +
            @"|(?:password|passwd|pwd)\s*(?:==|!=)\s*null" +
            // Property definitions: public string Password { get; set; }
            @"|(?:public|private|protected|internal)\s+string\??\s+(?:Password|Passwd|Pwd)\s*\{" +
            // Null/empty/default assignments
            @"|(?:password|passwd|pwd)\s*=\s*(?:null|default|string\.Empty|String\.Empty)" +
            // Attribute decorators: [Password], [DataType(DataType.Password)]
            @"|\[(?:Password|DataType\s*\(\s*DataType\.Password)" +
            // Hashing/validation utility methods
            @"|(?:Hash|Validate|Verify|Check|Reset|Change|Encode|Decode|Encrypt|Decrypt)Password" +
        @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // ── Attack surface patterns ─────────────────────────────────────────

    private static readonly Regex SqlInjectionPattern = new(
        @"(?:ExecuteAsync|QueryAsync|QueryFirstAsync|QuerySingleAsync|FromSqlRaw|SqlCommand)(?:<[^>]+>)?\s*\(\s*\$""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex UnsafeDeserializationPattern = new(
        @"(?:BinaryFormatter|JavaScriptSerializer|TypeNameHandling\s*\.\s*(?:All|Auto|Objects|Arrays))",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex PermissiveCorsPattern = new(
        @"(?:AllowAnyOrigin\s*\(|SetIsOriginAllowed\s*\(\s*[^)]*=>\s*true)",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex AllowCredentialsPattern = new(
        @"AllowCredentials\s*\(",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex TlsValidationBypassPattern = new(
        @"(?:" +
            @"(?:ServerCertificateCustomValidationCallback|RemoteCertificateValidationCallback)\s*=\s*(?:HttpClientHandler\.)?DangerousAcceptAnyServerCertificateValidator" +
            @"|(?:ServerCertificateCustomValidationCallback|RemoteCertificateValidationCallback)\s*=\s*(?:\([^)]*\)|\w+)\s*=>\s*true" +
            @"|(?:ServerCertificateCustomValidationCallback|RemoteCertificateValidationCallback)[\s\S]{0,160}return\s+true\s*;" +
        @")",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex InsecureHttpEndpointPattern = new(
        @"[""']http://(?!localhost(?=[:/""'])|127\.0\.0\.1(?=[:/""'])|0\.0\.0\.0(?=[:/""'])|host\.docker\.internal(?=[:/""'])|\[::1\])[^""'\s]+[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly HashSet<string> NonProductionPathMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "__tests__", "fixture", "fixtures",
        "sample", "samples", "example", "examples", "mock", "mocks",
        "stub", "stubs", "demo", "demos", "benchmark", "benchmarks"
    };

    private static readonly HashSet<string> KnownTestPackageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "coverlet.collector",
        "xunit",
        "xunit.runner.visualstudio",
        "NUnit",
        "NUnit3TestAdapter",
        "MSTest.TestAdapter",
        "MSTest.TestFramework",
        "FluentAssertions",
        "Shouldly",
        "Moq",
        "NSubstitute",
        "FakeItEasy"
    };

    // ── Vulnerability cache (package@version → advisories) ─────────────

    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, List<VulnAdvisory> Advisories)> VulnCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static DateTime _lastCacheEviction = DateTime.UtcNow;

    public async Task<SecurityScanResult> ScanAsync(string projectName, string repoPath, CancellationToken ct = default)
    {
        logger.LogInformation("Starting security scan for {Project}", projectName);
        var sw = Stopwatch.StartNew();

        var findings = new ConcurrentBag<SecurityFinding>();

        // Run all three pillars in parallel
        var secretsTask = Task.Run(() => ScanForSecrets(repoPath, findings), ct);
        var vulnTask = ScanVulnerablePackagesAsync(projectName, repoPath, findings, ct);
        var attackTask = Task.Run(() => ScanAttackSurface(repoPath, findings), ct);

        await Task.WhenAll(secretsTask, vulnTask, attackTask);

        var findingsList = findings.ToList();
        var score = ComputeSecurityScore(findingsList);
        var criticalCount = findingsList.Count(f => f.Severity == "critical");
        var highCount = findingsList.Count(f => f.Severity == "high");
        var mediumCount = findingsList.Count(f => f.Severity == "medium");
        var lowCount = findingsList.Count(f => f.Severity == "low");

        // Persist findings
        var now = DateTime.UtcNow;
        var entities = findingsList.Select(f => new SecurityFindingEntity
        {
            Project = projectName,
            Category = f.Category,
            Severity = f.Severity,
            Title = f.Title,
            Description = f.Description,
            FilePath = f.FilePath,
            LineNumber = f.LineNumber,
            Package = f.Package,
            PackageVersion = f.PackageVersion,
            Advisory = f.Advisory,
            ComputedAt = now
        }).ToList();

        await store.DeleteSecurityFindingsAsync(projectName);
        if (entities.Count > 0)
            await store.UpsertSecurityFindingsBatchAsync(projectName, entities);

        await store.UpsertProjectSecuritySummaryAsync(new ProjectSecuritySummaryEntity
        {
            Project = projectName,
            SecurityScore = score,
            CriticalCount = criticalCount,
            HighCount = highCount,
            MediumCount = mediumCount,
            LowCount = lowCount,
            ComputedAt = now
        });

        sw.Stop();
        logger.LogInformation(
            "Security scan for {Project}: score={Score:F1}, findings={Count} (C:{Critical} H:{High} M:{Medium} L:{Low}) in {Elapsed}ms",
            projectName, score, findingsList.Count, criticalCount, highCount, mediumCount, lowCount, sw.ElapsedMilliseconds);

        EvictExpiredCacheEntries();

        return new SecurityScanResult(score, criticalCount, highCount, mediumCount, lowCount, findingsList);
    }

    private static void EvictExpiredCacheEntries()
    {
        // Only run eviction every hour to avoid lock contention
        if (DateTime.UtcNow - _lastCacheEviction < TimeSpan.FromHours(1))
            return;
        _lastCacheEviction = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        foreach (var key in VulnCache.Keys)
        {
            if (VulnCache.TryGetValue(key, out var entry) && now - entry.CachedAt >= CacheTtl)
                VulnCache.TryRemove(key, out _);
        }
    }

    // ── Pillar 1: Secret Detection ──────────────────────────────────────

    private void ScanForSecrets(string repoPath, ConcurrentBag<SecurityFinding> findings)
    {
        if (!sourceFileProvider.RootExists(repoPath)) return;

        foreach (var file in sourceFileProvider.EnumerateSourceFiles(repoPath))
        {
            ScanFileForSecrets(file, findings);
        }
    }

    internal static void ScanFileForSecrets(SourceFile file, ConcurrentBag<SecurityFinding> findings)
    {
        if (ShouldSkipFileForSecurityScan(file.RelativePath)) return;

        var isSensitiveFile = IsSensitiveConfigFile(file.RelativePath);

        for (int lineNum = 0; lineNum < file.Lines.Length; lineNum++)
        {
            var line = file.Lines[lineNum];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//")) continue;

            foreach (var (name, pattern, severity, extFilter, entropyCheck) in SecretPatterns)
            {
                if (extFilter != null && !file.Extension.Equals(extFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                Match match;
                try
                {
                    match = pattern.Match(line);
                    if (!match.Success) continue;
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                // Skip placeholders and false positives
                if (PlaceholderPattern.IsMatch(line)) continue;
                try { if (FalsePositivePattern.IsMatch(line)) continue; }
                catch (RegexMatchTimeoutException) { }

                // For entropy-checked patterns in non-sensitive files,
                // verify the captured value actually looks like a secret
                if (entropyCheck && !isSensitiveFile)
                {
                    var capturedValue = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                    if (!LooksLikeSecret(capturedValue)) continue;
                }

                findings.Add(new SecurityFinding(
                    Category: "secret",
                    Severity: severity,
                    Title: $"{name} detected",
                    Description: $"Possible embedded {name.ToLowerInvariant()} found in source code",
                    FilePath: file.RelativePath,
                    LineNumber: lineNum + 1,
                    Package: null,
                    PackageVersion: null,
                    Advisory: null));
            }
        }
    }

    // ── Pillar 2: Vulnerable NuGet Packages ─────────────────────────────

    private async Task ScanVulnerablePackagesAsync(string projectName, string repoPath, ConcurrentBag<SecurityFinding> findings, CancellationToken ct)
    {
        try
        {
            var packages = await GetPackagesForVulnerabilityScanAsync(projectName, repoPath, ct);

            logger.LogInformation("Checking {Count} NuGet packages for vulnerabilities in {Project}",
                packages.Count, projectName);

            if (packages.Count == 0) return;

            using var client = httpClientFactory.CreateClient("nuget-vuln");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeGraph/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            // Check in batches to avoid overwhelming the API
            foreach (var package in packages)
            {
                ct.ThrowIfCancellationRequested();

                var cacheKey = $"{package.Name}@{package.Version}";

                // Check cache
                if (VulnCache.TryGetValue(cacheKey, out var cached) &&
                    DateTime.UtcNow - cached.CachedAt < CacheTtl)
                {
                    AddVulnFindings(findings, package.Name, package.Version, cached.Advisories);
                    continue;
                }

                try
                {
                    var advisories = await CheckNuGetVulnerabilitiesAsync(client, package.Name, package.Version, ct);
                    VulnCache[cacheKey] = (DateTime.UtcNow, advisories);
                    AddVulnFindings(findings, package.Name, package.Version, advisories);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to check vulnerabilities for {Package}@{Version}",
                        package.Name, package.Version);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NuGet vulnerability scan failed for {Project}", projectName);
        }
    }

    private async Task<List<VulnAdvisory>> CheckNuGetVulnerabilitiesAsync(
        HttpClient client, string packageName, string version, CancellationToken ct)
    {
        var advisories = new List<VulnAdvisory>();
        var loweredName = packageName.ToLowerInvariant();

        // Query NuGet registration endpoint which includes vulnerability info
        var url = $"https://api.nuget.org/v3/registration5-semver2/{loweredName}/index.json";

        var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return advisories;

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        // Navigate registration pages to find our version and check for vulnerabilities
        if (!doc.RootElement.TryGetProperty("items", out var pages)) return advisories;

        foreach (var page in pages.EnumerateArray())
        {
            // Pages may need to be fetched separately
            JsonElement items;
            if (page.TryGetProperty("items", out items))
            {
                // Items inline
            }
            else if (page.TryGetProperty("@id", out var pageUrl))
            {
                // Fetch page
                try
                {
                    var pageResponse = await client.GetAsync(pageUrl.GetString(), ct);
                    if (!pageResponse.IsSuccessStatusCode) continue;
                    var pageJson = await pageResponse.Content.ReadAsStringAsync(ct);
                    var pageDoc = JsonDocument.Parse(pageJson);
                    if (!pageDoc.RootElement.TryGetProperty("items", out items)) continue;
                }
                catch { continue; }
            }
            else continue;

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("catalogEntry", out var entry)) continue;

                var entryVersion = entry.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (!string.Equals(entryVersion, version, StringComparison.OrdinalIgnoreCase)) continue;

                // Check for vulnerabilities on this version
                if (entry.TryGetProperty("vulnerabilities", out var vulns))
                {
                    foreach (var vuln in vulns.EnumerateArray())
                    {
                        var advisoryUrl = vuln.TryGetProperty("advisoryUrl", out var au) ? au.GetString() : null;
                        var severityRaw = vuln.TryGetProperty("severity", out var sv) ? sv.GetString() : "medium";

                        // NuGet uses numeric severity: 0=low, 1=moderate, 2=high, 3=critical
                        var severity = severityRaw switch
                        {
                            "0" or "Low" => "low",
                            "1" or "Moderate" => "medium",
                            "2" or "High" => "high",
                            "3" or "Critical" => "critical",
                            _ => "medium"
                        };

                        advisories.Add(new VulnAdvisory(severity, advisoryUrl ?? ""));
                    }
                }

                break; // Found our version
            }
        }

        return advisories;
    }

    private static void AddVulnFindings(ConcurrentBag<SecurityFinding> findings,
        string packageName, string version, List<VulnAdvisory> advisories)
    {
        foreach (var advisory in advisories)
        {
            findings.Add(new SecurityFinding(
                Category: "vulnerable_package",
                Severity: advisory.Severity,
                Title: $"Vulnerable package: {packageName} {version}",
                Description: $"NuGet package {packageName}@{version} has a known vulnerability",
                FilePath: null,
                LineNumber: null,
                Package: packageName,
                PackageVersion: version,
                Advisory: advisory.AdvisoryUrl));
        }
    }

    private async Task<List<(string Name, string Version)>> GetPackagesForVulnerabilityScanAsync(
        string projectName, string repoPath, CancellationToken ct)
    {
        var localPackages = GetPackagesFromProjectFiles(repoPath);
        if (localPackages.HasProjectFiles)
            return localPackages.Packages;

        // Fallback to graph when a local checkout isn't available.
        var nugetNodes = await store.SearchNodesAsync(
            projectName, "%", label: NodeLabel.NuGetPackage, limit: 500);

        return nugetNodes
            .Select(n => (
                Name: n.Name,
                Version: n.Properties.TryGetValue("version", out var v) ? v?.ToString() ?? "" : ""))
            .Where(p => !string.IsNullOrEmpty(p.Version))
            .Distinct()
            .ToList();
    }

    private (bool HasProjectFiles, List<(string Name, string Version)> Packages) GetPackagesFromProjectFiles(string repoPath)
    {
        if (!fileSystem.DirectoryExists(repoPath))
            return (false, []);

        var csprojFiles = fileSystem.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories).ToArray();
        if (csprojFiles.Length == 0)
            return (false, []);

        var packageRefs = new List<(string Name, string Version, bool IsTestProject)>();

        foreach (var csproj in csprojFiles)
        {
            try
            {
                var projectXml = fileSystem.ReadAllText(csproj);
                var refs = nugetReferenceExtractor.ExtractFromProjectXml(projectXml);
                var relativePath = fileSystem.GetRelativePath(repoPath, csproj);
                var isTestProject = IsTestProjectFile(relativePath, projectXml, refs);

                packageRefs.AddRange(refs.Select(r => (r.PackageName, r.Version, isTestProject)));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to inspect project file {Csproj} for package vulnerability filtering", csproj);
            }
        }

        return (true, FilterPackagesForVulnerabilityScan(packageRefs));
    }

    internal static bool IsTestProjectFile(
        string relativePath,
        string projectXml,
        IReadOnlyList<(string PackageName, string Version)> packageReferences)
    {
        if (ShouldSkipFileForSecurityScan(relativePath))
            return true;

        if (Regex.IsMatch(projectXml, @"<IsTestProject>\s*true\s*</IsTestProject>", RegexOptions.IgnoreCase, RegexTimeout))
            return true;

        return packageReferences.Any(p => KnownTestPackageNames.Contains(p.PackageName));
    }

    internal static List<(string Name, string Version)> FilterPackagesForVulnerabilityScan(
        IEnumerable<(string Name, string Version, bool IsTestProject)> packageReferences)
    {
        return packageReferences
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Version))
            .GroupBy(p => (p.Name, p.Version), StringTupleComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(p => !p.IsTestProject))
            .Select(g => (g.Key.Name, g.Key.Version))
            .ToList();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string Version)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string Name, string Version) x, (string Name, string Version) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, string Version) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version));
    }

    // ── Pillar 3: Attack Surface Analysis ───────────────────────────────

    private void ScanAttackSurface(string repoPath, ConcurrentBag<SecurityFinding> findings)
    {
        if (!sourceFileProvider.RootExists(repoPath)) return;

        foreach (var file in sourceFileProvider.EnumerateSourceFiles(repoPath))
        {
            ScanFileForAttackSurface(file, findings);
        }
    }

    internal static void ScanFileForAttackSurface(SourceFile file, ConcurrentBag<SecurityFinding> findings)
    {
        if (ShouldSkipFileForSecurityScan(file.RelativePath)) return;

        var content = string.Join('\n', file.Lines);
        var isCodeFile = file.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);

        if (isCodeFile)
        {
            // Check for SQL injection patterns (string interpolation in query methods)
            for (int i = 0; i < file.Lines.Length; i++)
            {
                var line = file.Lines[i];

                try
                {
                    if (SqlInjectionPattern.IsMatch(line))
                    {
                        findings.Add(new SecurityFinding(
                            Category: "attack_surface",
                            Severity: "high",
                            Title: "Potential SQL injection",
                            Description: "String interpolation used directly in a database query method. Use parameterized queries instead.",
                            FilePath: file.RelativePath,
                            LineNumber: i + 1,
                            Package: null, PackageVersion: null, Advisory: null));
                    }
                }
                catch (RegexMatchTimeoutException) { }

                try
                {
                    if (UnsafeDeserializationPattern.IsMatch(line))
                    {
                        findings.Add(new SecurityFinding(
                            Category: "attack_surface",
                            Severity: "high",
                            Title: "Unsafe deserialization",
                            Description: "Use of unsafe deserialization method that could allow remote code execution",
                            FilePath: file.RelativePath,
                            LineNumber: i + 1,
                            Package: null, PackageVersion: null, Advisory: null));
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }
        }

        if (isCodeFile)
        {
            try
            {
                var corsMatch = PermissiveCorsPattern.Match(content);
                if (corsMatch.Success)
                {
                    var allowsCredentials = AllowCredentialsPattern.IsMatch(content);
                    findings.Add(new SecurityFinding(
                        Category: "attack_surface",
                        Severity: allowsCredentials ? "high" : "medium",
                        Title: allowsCredentials ? "CORS allows any origin with credentials" : "Permissive CORS policy",
                        Description: allowsCredentials
                            ? "CORS policy appears to allow credentials while broadly trusting origins. Restrict origins explicitly."
                            : "CORS policy allows any origin. Restrict origins explicitly for non-public APIs.",
                        FilePath: file.RelativePath,
                        LineNumber: GetLineNumberFromIndex(content, corsMatch.Index),
                        Package: null, PackageVersion: null, Advisory: null));
                }
            }
            catch (RegexMatchTimeoutException) { }
        }

        if (isCodeFile)
        {
            try
            {
                var tlsMatch = TlsValidationBypassPattern.Match(content);
                if (tlsMatch.Success)
                {
                    findings.Add(new SecurityFinding(
                        Category: "attack_surface",
                        Severity: "high",
                        Title: "TLS certificate validation bypass",
                        Description: "HTTP client code appears to disable certificate validation, which can enable man-in-the-middle attacks.",
                        FilePath: file.RelativePath,
                        LineNumber: GetLineNumberFromIndex(content, tlsMatch.Index),
                        Package: null, PackageVersion: null, Advisory: null));
                }
            }
            catch (RegexMatchTimeoutException) { }
        }

        try
        {
            var httpMatch = InsecureHttpEndpointPattern.Match(content);
            if (httpMatch.Success)
            {
                findings.Add(new SecurityFinding(
                    Category: "attack_surface",
                    Severity: "medium",
                    Title: "Non-local HTTP endpoint",
                    Description: "Code appears to call a non-local service over plain HTTP. Prefer HTTPS unless the endpoint is intentionally trusted and isolated.",
                    FilePath: file.RelativePath,
                    LineNumber: GetLineNumberFromIndex(content, httpMatch.Index),
                    Package: null, PackageVersion: null, Advisory: null));
            }
        }
        catch (RegexMatchTimeoutException) { }

        // Check for controllers without auth
        if (isCodeFile && (content.Contains("[ApiController]") || content.Contains("[Route(")))
        {
            var hasClassAuth = content.Contains("[Authorize") || content.Contains("[AllowAnonymous");
            if (!hasClassAuth)
            {
                for (int i = 0; i < file.Lines.Length; i++)
                {
                    if (file.Lines[i].Contains("class ") && (file.Lines[i].Contains("Controller") || file.Lines[i].Contains(": ControllerBase")))
                    {
                        findings.Add(new SecurityFinding(
                            Category: "attack_surface",
                            Severity: "medium",
                            Title: "Controller without authorization",
                            Description: "API controller has no [Authorize] attribute — all endpoints are publicly accessible",
                            FilePath: file.RelativePath,
                            LineNumber: i + 1,
                            Package: null, PackageVersion: null, Advisory: null));
                        break;
                    }
                }
            }
        }
    }

    // ── Sensitive file detection ────────────────────────────────────────
    // These files commonly hold real credentials — flag aggressively, skip entropy.

    internal static bool IsSensitiveConfigFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath).ToLowerInvariant();

        if (fileName.StartsWith("appsettings") && fileName.EndsWith(".json")) return true;
        if (fileName.StartsWith(".env")) return true;
        if (fileName is "web.config" or "app.config" or "environment.config") return true;
        if (fileName is "launchsettings.json" or "secrets.json") return true;
        if (fileName.StartsWith("docker-compose") && (fileName.EndsWith(".yml") || fileName.EndsWith(".yaml"))) return true;

        return false;
    }

    internal static bool ShouldSkipFileForSecurityScan(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(ContainsNonProductionMarker);
    }

    private static bool ContainsNonProductionMarker(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        if (NonProductionPathMarkers.Contains(segment)) return true;

        var tokens = segment.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(NonProductionPathMarkers.Contains);
    }

    // ── Entropy / structure analysis ─────────────────────────────────────
    // Distinguishes real secrets ("Admin@2024!", "#MySuperSecret#") from
    // identifier names ("ResetClientPassword", "SendForgotPassword").

    internal static bool LooksLikeSecret(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 3) return false;

        var hasDigits = value.Any(char.IsDigit);
        var hasPasswordSpecial = value.Any(IsPasswordSpecialChar);
        var hasUpper = value.Any(char.IsUpper);
        var hasLower = value.Any(char.IsLower);

        // Special characters typical of passwords → strong signal
        if (hasPasswordSpecial && value.Length >= 6) return true;

        // Mixes all three character classes (upper + lower + digits) → likely a credential
        if (hasDigits && hasUpper && hasLower && ComputeShannonEntropy(value) > 2.5) return true;

        // Pure letters in camelCase/PascalCase → identifier or route name, not a secret
        if (!hasDigits && !hasPasswordSpecial && IsCamelOrPascalCase(value)) return false;

        // High entropy catch-all for anything else
        return ComputeShannonEntropy(value) > 4.0;
    }

    /// <summary>Characters commonly found in passwords but not in identifiers or paths.</summary>
    private static bool IsPasswordSpecialChar(char c) =>
        c is '!' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')' or '+'
            or '=' or '{' or '}' or '[' or ']' or '|' or '\\' or ':' or ';' or '<'
            or '>' or '?' or ',' or '~';

    private static bool IsCamelOrPascalCase(string value)
    {
        // Multiple uppercase letters with lowercase between them → word boundaries
        int upperCount = value.Count(char.IsUpper);
        return upperCount >= 2 && upperCount < value.Length && value.All(char.IsLetter);
    }

    internal static double ComputeShannonEntropy(string value)
    {
        Span<int> freq = stackalloc int[128];
        int asciiCount = 0;
        foreach (var c in value)
        {
            if (c < 128) { freq[c]++; asciiCount++; }
        }
        if (asciiCount == 0) return 0;

        double entropy = 0;
        double len = asciiCount;
        for (int i = 0; i < 128; i++)
        {
            if (freq[i] == 0) continue;
            var p = freq[i] / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private static int GetLineNumberFromIndex(string content, int matchIndex)
    {
        var boundedIndex = Math.Clamp(matchIndex, 0, content.Length);
        var line = 1;
        for (var i = 0; i < boundedIndex; i++)
        {
            if (content[i] == '\n')
                line++;
        }

        return line;
    }

    // ── Scoring ─────────────────────────────────────────────────────────

    private static double ComputeSecurityScore(IReadOnlyList<SecurityFinding> findings)
    {
        var secretPenalty = ComputeCategoryPenalty(
            findings.Where(f => f.Category == "secret"),
            f => f.FilePath ?? $"{f.Title}:{f.LineNumber ?? 0}",
            cap: 6.5);

        var attackSurfacePenalty = ComputeCategoryPenalty(
            findings.Where(f => f.Category == "attack_surface"),
            f => f.FilePath ?? $"{f.Title}:{f.LineNumber ?? 0}",
            cap: 4.0);

        var packagePenalty = ComputeCategoryPenalty(
            findings.Where(f => f.Category == "vulnerable_package"),
            f => $"{f.Package ?? f.Title}@{f.PackageVersion ?? ""}",
            cap: 4.5);

        var score = 10.0 - secretPenalty - attackSurfacePenalty - packagePenalty;
        return Math.Round(Math.Clamp(score, 1.0, 10.0), 1);
    }

    private static double ComputeCategoryPenalty(IEnumerable<SecurityFinding> findings,
        Func<SecurityFinding, string> scopeKeySelector, double cap)
    {
        double penalty = 0;

        foreach (var scope in findings
                     .GroupBy(scopeKeySelector)
                     .Select(g => g.Select(f => SeverityWeight(f.Severity)).OrderDescending().ToList()))
        {
            if (scope.Count == 0) continue;

            penalty += scope[0];
            for (var i = 1; i < scope.Count; i++)
                penalty += scope[i] * 0.6;
        }

        return Math.Min(penalty, cap);
    }

    private static double SeverityWeight(string severity) => severity switch
    {
        "critical" => 2.5,
        "high" => 1.5,
        "medium" => 0.7,
        "low" => 0.3,
        _ => 0.5
    };

    private record VulnAdvisory(string Severity, string AdvisoryUrl);
}
