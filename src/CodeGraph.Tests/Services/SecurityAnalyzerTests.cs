using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Tests.Extractors;

namespace CodeGraph.Tests.Services;

public class SecurityAnalyzerTests
{
    private readonly InMemoryGraphStore _store;
    private readonly InMemorySourceFileProvider _files;
    private readonly SecurityAnalyzer _analyzer;

    public SecurityAnalyzerTests()
    {
        _store = new InMemoryGraphStore();
        _files = new InMemorySourceFileProvider();
        var httpFactory = new StubHttpClientFactory();
        _analyzer = new SecurityAnalyzer(
            _store,
            httpFactory,
            new LocalFileSystem(),
            new NuGetReferenceExtractor(),
            _files,
            NullLogger<SecurityAnalyzer>.Instance);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<SecurityScanResult> ScanAsync()
    {
        return await _analyzer.ScanAsync("TestProject", "repo");
    }

    private static SecurityFinding? Find(SecurityScanResult result, string category, string titleContains) =>
        result.Findings.FirstOrDefault(f =>
            f.Category == category &&
            f.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));

    // ═══════════════════════════════════════════════════════════════════
    //  SECRET DETECTION — True Positives
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DetectsAwsAccessKey()
    {
        _files.AddFile("src/config.cs", """
            var key = "AKIAI44QH8DHBKUPLDRE";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "AWS");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("critical");
        finding!.FilePath!.ShouldContain("config.cs");
    }

    [Fact]
    public async Task DetectsPrivateKey()
    {
        _files.AddFile("src/key.cs", """
            var pem = "-----BEGIN RSA PRIVATE KEY-----";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Private Key");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("critical");
    }

    [Fact]
    public async Task DetectsConnectionStringPassword()
    {
        _files.AddFile("src/appsettings.json", """
            { "ConnectionString": "Server=db;Database=mydb;User=admin;password=SuperSecret123" }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Connection String");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("high");
    }

    [Fact]
    public async Task DetectsGenericApiKey()
    {
        _files.AddFile("src/settings.cs", """
            var apiKey = "ABCDEFGHIJKLMNOPQRSTuvwxyz123456";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "API Key");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("medium");
    }

    [Fact]
    public async Task DetectsJwtSecret()
    {
        _files.AddFile("src/auth.cs", """
            var jwt_secret = "mySuper$ecretSigningKey2024!@#";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "JWT");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("high");
    }

    [Fact]
    public async Task DetectsHardcodedPasswordInCsharp()
    {
        _files.AddFile("src/login.cs", """
            var password = "Admin@2024!";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Hardcoded Password");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("high");
        finding!.FilePath!.ShouldContain("login.cs");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SECRET DETECTION — False Positives (should NOT flag)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IgnoresPlaceholderValues()
    {
        _files.AddFile("src/example.cs", """
            var password = "changeme";
            var apiKey = "your_key_here_placeholder_example";
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresEmptyPasswordAssignment()
    {
        _files.AddFile("src/model.cs", """
            string password = "";
            var pass = "";
            var pwd = '';
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresInterfaceMethodSignatures()
    {
        _files.AddFile("src/IEncryptDecrypt.cs", """
            namespace Example.Common.Encryption
            {
                public interface IEncryptDecrypt
                {
                    string EncryptValForUrl(string clearText, string password = "");
                    string DecryptValFromUrl(string clearText, string password = "");
                    string Encrypt(string clearText, string password = "");
                    string Decrypt(string cipherText, string password = "");
                    string DecryptIfEncrypted(string cipherText, string pass = "");
                }
            }
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresPropertyDefinitions()
    {
        _files.AddFile("src/UserModel.cs", """
            public class UserModel
            {
                public string Password { get; set; }
                public string? Pwd { get; set; }
            }
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresPropertyAccessAssignment()
    {
        _files.AddFile("src/Handler.cs", """
            var password = request.Password;
            user.Password = model.Password;
            dto.Pwd = entity.Pwd;
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresNullAndEmptyChecks()
    {
        _files.AddFile("src/Validator.cs", """
            if (string.IsNullOrEmpty(password)) throw new Exception();
            if (password == null) return;
            if (pwd != null) Process(pwd);
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresPasswordUtilityMethods()
    {
        _files.AddFile("src/AuthService.cs", """
            var hash = HashPassword(input);
            var valid = ValidatePassword(attempt, stored);
            var ok = VerifyPassword(raw);
            ResetPassword(userId);
            EncryptPassword(clearText);
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresConfigLookups()
    {
        _files.AddFile("src/Startup.cs", """
            var pwd = Configuration["password"];
            var pass = config.GetValue("password");
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresNullDefaultAssignments()
    {
        _files.AddFile("src/Reset.cs", """
            password = null;
            password = string.Empty;
            password = default;
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresMethodParameterDeclarations()
    {
        _files.AddFile("src/Service.cs", """
            public void Login(string username, string password)
            {
            }
            public bool Verify(string password, string hash) => true;
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresVariableDeclarations()
    {
        _files.AddFile("src/Handler.cs", """
            string password = GetPassword();
            var pass = ReadLine();
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresRouteConstantsContainingPasswordInName()
    {
        _files.AddFile("src/Routes.cs", """
            static readonly resetClientPassword = 'ResetClientPassword';
            static readonly forgotClientPassword = 'SendForgotPassword';
            const string changePasswordRoute = "ChangePassword";
            private static string resetPasswordEndpoint = "api/reset-password";
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresCamelCasePasswordValueInNonConfigFile()
    {
        // Even with standalone "password =", if the VALUE is a camelCase identifier, skip it
        _files.AddFile("src/Constants.cs", """
            var password = "ResetClientPassword";
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task FlagsRealSecretInNonConfigFile()
    {
        _files.AddFile("src/DbHelper.cs", """
            var password = "Admin@2024!";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Hardcoded Password");
        finding.ShouldNotBeNull();
    }

    [Fact]
    public async Task FlagsAnythingSuspiciousInSensitiveConfigFile()
    {
        // In appsettings.json, even a camelCase value should be flagged — better safe than sorry
        _files.AddFile("src/appsettings.json", """
            { "DbPassword": "password=ResetClientPassword" }
            """);

        var result = await ScanAsync();

        Find(result, "secret", "Connection String").ShouldNotBeNull();
    }

    [Fact]
    public async Task FlagsSecretsInDotEnvFile()
    {
        _files.AddFile(".env", """
            DB_PASSWORD=SuperSecret123
            """);

        var result = await ScanAsync();

        Find(result, "secret", "Connection String").ShouldNotBeNull();
    }

    [Fact]
    public async Task FlagsConnectionStringWithRealPassword()
    {
        // dbConnectionPassword with a real-looking secret — special chars trigger entropy check
        _files.AddFile("src/Config.cs", """
            var dbConnectionPassword = "#MySuperSecretPassword#";
            """);

        var result = await ScanAsync();

        Find(result, "secret", "Hardcoded Password").ShouldNotBeNull();
    }

    [Fact]
    public async Task IgnoresSecretsInTestProjects()
    {
        _files.AddFile("src/CodeGraph.Tests/Services/SecurityAnalyzerTests.cs", """
            var key = "AKIAI44QH8DHBKUPLDRE";
            var jwt_secret = "mySuper$ecretSigningKey2024!@#";
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    [Fact]
    public async Task DetectsApiKeyInJsonConfig()
    {
        _files.AddFile("src/appsettings.json", """
            { "ApiKey": "sk-abcdefghijklmnopqrstuvwxyz123456" }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "API Key");
        finding.ShouldNotBeNull();
        finding!.FilePath.ShouldBe("src/appsettings.json");
    }

    [Fact]
    public async Task DetectsProviderApiTokenWithoutSecretNamedVariable()
    {
        _files.AddFile("src/OpenAiClient.cs", """
            var modelCredential = "sk-proj-abcdefghijklmnopqrstuvwxyz1234567890";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Service API Token");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("critical");
    }

    [Fact]
    public async Task DetectsConfigSecretField()
    {
        _files.AddFile("src/appsettings.json", """
            { "clientSecret": "sup3r-Secret-Client-Key-2026" }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Config Secret");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("high");
    }

    [Fact]
    public async Task DetectsCredentialedUrl()
    {
        _files.AddFile("src/appsettings.json", """
            { "WebhookUrl": "https://service-user:Sup3rSecret!@api.example.com/hook" }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "Credentialed URL");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("high");
    }

    // ── Entropy helper unit tests ────────────────────────────────────────

    [Theory]
    [InlineData("Admin@2024!", true)]           // special chars + length
    [InlineData("#MySuperSecretPassword#", true)] // special chars
    [InlineData("SuperSecret123", true)]         // mixed upper+lower+digits
    [InlineData("ResetClientPassword", false)]   // camelCase identifier
    [InlineData("SendForgotPassword", false)]    // camelCase identifier
    [InlineData("ChangePassword", false)]        // camelCase identifier
    [InlineData("ab", false)]                    // too short
    public void LooksLikeSecret_ClassifiesCorrectly(string value, bool expected)
    {
        SecurityAnalyzer.LooksLikeSecret(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Development.json", true)]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData("web.config", true)]
    [InlineData("secrets.json", true)]
    [InlineData("docker-compose.yml", true)]
    [InlineData("src/Services/MyService.cs", false)]
    [InlineData("src/Routes.cs", false)]
    public void IsSensitiveConfigFile_ClassifiesCorrectly(string path, bool expected)
    {
        SecurityAnalyzer.IsSensitiveConfigFile(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("src/CodeGraph.Tests/Services/SecurityAnalyzerTests.cs", true)]
    [InlineData("tests/fixtures/leaky-config.json", true)]
    [InlineData("src/examples/demo.ts", true)]
    [InlineData("src/CodeGraph.Api/Startup.cs", false)]
    [InlineData("src/Services/AuthService.cs", false)]
    public void ShouldSkipFileForSecurityScan_ClassifiesCorrectly(string path, bool expected)
    {
        SecurityAnalyzer.ShouldSkipFileForSecurityScan(path).ShouldBe(expected);
    }

    [Fact]
    public void IsTestProjectFile_DetectsIsTestProjectFlag()
    {
        var refs = new List<(string PackageName, string Version)>
        {
            ("Dapper", "2.1.0")
        };

        var result = SecurityAnalyzer.IsTestProjectFile(
            "src/MyProject/MyProject.csproj",
            """
            <Project>
              <PropertyGroup>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
            </Project>
            """,
            refs);

        result.ShouldBeTrue();
    }

    [Fact]
    public void IsTestProjectFile_DetectsTestPackageReferences()
    {
        var refs = new List<(string PackageName, string Version)>
        {
            ("Microsoft.NET.Test.Sdk", "17.11.1"),
            ("xunit", "2.9.2")
        };

        var result = SecurityAnalyzer.IsTestProjectFile(
            "src/MyProject/MyProject.csproj",
            "<Project />",
            refs);

        result.ShouldBeTrue();
    }

    [Fact]
    public void FilterPackagesForVulnerabilityScan_IgnoresPackagesOnlyUsedInTestProjects()
    {
        var packages = SecurityAnalyzer.FilterPackagesForVulnerabilityScan(
        [
            ("xunit", "2.9.2", true),
            ("coverlet.collector", "6.0.2", true),
            ("Dapper", "2.1.0", false),
            ("Shouldly", "4.3.0", true)
        ]);

        packages.Count.ShouldBe(1);
        packages.ShouldContain(("Dapper", "2.1.0"));
    }

    [Fact]
    public void FilterPackagesForVulnerabilityScan_KeepsPackagesSharedByProdAndTests()
    {
        var packages = SecurityAnalyzer.FilterPackagesForVulnerabilityScan(
        [
            ("Newtonsoft.Json", "13.0.3", true),
            ("Newtonsoft.Json", "13.0.3", false),
            ("xunit", "2.9.2", true)
        ]);

        packages.Count.ShouldBe(1);
        packages.ShouldContain(("Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public async Task IgnoresCommentedLines()
    {
        _files.AddFile("src/old.cs", """
            // password = "RealSecret123"
            // var apiKey = "AKIAI44QH8DHBKUPLDRE";
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SECRET DETECTION — Extension filtering
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HardcodedPasswordOnlyFlaggedInCsFiles()
    {
        // .cs file should be flagged
        _files.AddFile("src/login.cs", """
            var adminPassword = "Admin@2024!";
            """);
        // .json file should NOT be flagged for hardcoded password (ext filter = .cs)
        _files.AddFile("src/config.json", """
            var adminPassword = "Admin@2024!";
            """);

        var result = await ScanAsync();

        var findings = result.Findings.Where(f => f.Title.Contains("Hardcoded Password")).ToList();
        findings.Count.ShouldBe(1);
        findings[0].FilePath!.ShouldContain(".cs");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ATTACK SURFACE — True Positives
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DetectsSqlInjectionPattern()
    {
        _files.AddFile("src/Repo.cs", """
            public class MyRepo
            {
                public async Task<User> Get(string name)
                {
                    return await conn.QueryAsync<User>($"SELECT * FROM users WHERE name = {name}");
                }
            }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "SQL injection");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("high");
        finding!.FilePath!.ShouldContain("Repo.cs");
    }

    [Fact]
    public async Task DetectsUnsafeDeserialization_BinaryFormatter()
    {
        _files.AddFile("src/Serializer.cs", """
            public class Serializer
            {
                public object Deserialize(Stream s)
                {
                    var formatter = new BinaryFormatter();
                    return formatter.Deserialize(s);
                }
            }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "Unsafe deserialization");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("high");
    }

    [Fact]
    public async Task DetectsUnsafeDeserialization_TypeNameHandling()
    {
        _files.AddFile("src/JsonHelper.cs", """
            public class JsonHelper
            {
                var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
            }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "Unsafe deserialization");
        finding.ShouldNotBeNull();
    }

    [Fact]
    public async Task DetectsControllerWithoutAuthorize()
    {
        _files.AddFile("src/Controllers/PublicController.cs", """
            [ApiController]
            [Route("api/public")]
            public class PublicController : ControllerBase
            {
                [HttpGet]
                public IActionResult Get() => Ok();
            }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "Controller without authorization");
        finding.ShouldNotBeNull();
        finding.Severity.ShouldBe("medium");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ATTACK SURFACE — False Positives (should NOT flag)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task IgnoresControllerWithAuthorize()
    {
        _files.AddFile("src/Controllers/SecureController.cs", """
            [Authorize]
            [ApiController]
            [Route("api/secure")]
            public class SecureController : ControllerBase
            {
                [HttpGet]
                public IActionResult Get() => Ok();
            }
            """);

        var result = await ScanAsync();

        Find(result, "attack_surface", "Controller without authorization").ShouldBeNull();
    }

    [Fact]
    public async Task IgnoresParameterizedQueries()
    {
        _files.AddFile("src/SafeRepo.cs", """
            public class SafeRepo
            {
                public async Task<User> Get(string name)
                {
                    return await conn.QueryAsync<User>("SELECT * FROM users WHERE name = @name", new { name });
                }
            }
            """);

        var result = await ScanAsync();

        Find(result, "attack_surface", "SQL injection").ShouldBeNull();
    }

    [Fact]
    public async Task AttackSurfaceOnlyScannsCsFiles()
    {
        // SQL injection in a .json file should not be flagged (attack surface only scans .cs)
        _files.AddFile("src/data.json", """
            return await conn.QueryAsync<User>($"SELECT * FROM users WHERE name = {name}");
            """);

        var result = await ScanAsync();

        Find(result, "attack_surface", "SQL injection").ShouldBeNull();
    }

    [Fact]
    public async Task IgnoresAttackSurfaceExamplesInFixtureFiles()
    {
        _files.AddFile("tests/fixtures/PublicController.cs", """
            [ApiController]
            [Route("api/public")]
            public class PublicController : ControllerBase
            {
                [HttpGet]
                public IActionResult Get() => Ok();
            }
            """);

        var result = await ScanAsync();

        result.Findings.Where(f => f.Category == "attack_surface").ShouldBeEmpty();
    }

    [Fact]
    public async Task DetectsPermissiveCorsPolicy()
    {
        _files.AddFile("src/Startup.cs", """
            services.AddCors(options => options.AddPolicy("open", policy =>
                policy.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod()));
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "Permissive CORS");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("medium");
    }

    [Fact]
    public async Task DetectsTlsValidationBypass()
    {
        _files.AddFile("src/HttpClientFactory.cs", """
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "TLS certificate validation bypass");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("high");
    }

    [Fact]
    public async Task DetectsNonLocalHttpEndpointInConfig()
    {
        _files.AddFile("src/appsettings.json", """
            { "WebhookBaseUrl": "http://api.example.com/v1" }
            """);

        var result = await ScanAsync();

        var finding = Find(result, "attack_surface", "Non-local HTTP endpoint");
        finding.ShouldNotBeNull();
        finding!.Severity.ShouldBe("medium");
        finding.FilePath.ShouldBe("src/appsettings.json");
    }

    [Fact]
    public async Task IgnoresLocalHttpEndpointInConfig()
    {
        _files.AddFile("src/appsettings.json", """
            { "BaseUrl": "http://localhost:1234/v1" }
            """);

        var result = await ScanAsync();

        Find(result, "attack_surface", "Non-local HTTP endpoint").ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SCORING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CleanRepoScoresTen()
    {
        _files.AddFile("src/Clean.cs", """
            public class Clean
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        var result = await ScanAsync();

        result.SecurityScore.ShouldBe(10.0);
        result.CriticalCount.ShouldBe(0);
        result.HighCount.ShouldBe(0);
        result.MediumCount.ShouldBe(0);
        result.LowCount.ShouldBe(0);
        result.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CriticalFindingsDropScoreSignificantly()
    {
        _files.AddFile("src/bad.cs", """
            var key1 = "AKIAI44QH8DHBKUPLDRE";
            var pem = "-----BEGIN RSA PRIVATE KEY-----";
            """);

        var result = await ScanAsync();

        result.CriticalCount.ShouldBeGreaterThanOrEqualTo(2);
        result.SecurityScore.ShouldBeLessThanOrEqualTo(6.0);
    }

    [Fact]
    public async Task ScoreNeverDropsBelowOne()
    {
        _files.AddFile("src/terrible.cs", """
            var k1 = "AKIAI44QH8DHBKUPLDRE1";
            var k2 = "AKIAI44QH8DHBKUPLDRE2";
            var k3 = "AKIAI44QH8DHBKUPLDRE3";
            var k4 = "AKIAI44QH8DHBKUPLDRE4";
            var pem = "-----BEGIN RSA PRIVATE KEY-----";
            var password = "HardcodedAdmin123!";
            await conn.QueryAsync<User>($"SELECT * FROM u WHERE id = {id}");
            var bf = new BinaryFormatter();
            """);

        var result = await ScanAsync();

        result.SecurityScore.ShouldBeGreaterThanOrEqualTo(1.0);
    }

    [Fact]
    public async Task RepeatedFindingsInOneFileScoreBetterThanSpreadAcrossFiles()
    {
        _files.AddFile("src/one-place.cs", """
            var key1 = "AKIAI44QH8DHBKUPLDRE";
            var key2 = "AKIAI44QH8DHBKUPLDRF";
            var key3 = "AKIAI44QH8DHBKUPLDRG";
            """);

        var concentrated = await ScanAsync();

        _files.AddFile("src/one.cs", """var key = "AKIAI44QH8DHBKUPLDRH";""");
        _files.AddFile("src/two.cs", """var key = "AKIAI44QH8DHBKUPLDRI";""");
        _files.AddFile("src/three.cs", """var key = "AKIAI44QH8DHBKUPLDRJ";""");
        _files.AddFile("src/one-place.cs", "// cleaned");

        var spread = await ScanAsync();

        concentrated.CriticalCount.ShouldBe(3);
        spread.CriticalCount.ShouldBe(3);
        spread.SecurityScore.ShouldBeLessThan(concentrated.SecurityScore);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PERSISTENCE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PersistsFindingsToStore()
    {
        _files.AddFile("src/leak.cs", """
            var key = "AKIAI44QH8DHBKUPLDRE";
            """);

        await ScanAsync();

        var stored = await _store.GetSecurityFindingsAsync("TestProject");
        stored.ShouldNotBeEmpty();
        stored.ShouldContain(f => f.Category == "secret");
    }

    [Fact]
    public async Task PersistsSecuritySummaryToStore()
    {
        _files.AddFile("src/leak.cs", """
            var key = "AKIAI44QH8DHBKUPLDRE";
            """);

        await ScanAsync();

        var summary = await _store.GetProjectSecuritySummaryAsync("TestProject");
        summary.ShouldNotBeNull();
        summary.SecurityScore.ShouldBeLessThan(10.0);
        summary.CriticalCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ClearsPreviousFindingsOnRescan()
    {
        _files.AddFile("src/leak.cs", """
            var key = "AKIAI44QH8DHBKUPLDRE";
            """);

        var firstResult = await ScanAsync();
        firstResult.Findings.Count.ShouldBeGreaterThan(0);
        firstResult.Findings.ShouldContain(f => f.Category == "secret");

        // Replace file content with clean code and rescan
        _files.AddFile("src/leak.cs", "// cleaned up");
        var secondResult = await ScanAsync();

        secondResult.Findings.Where(f => f.Category == "secret").ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FINDING METADATA
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FindingsIncludeFilePathAndLineNumber()
    {
        _files.AddFile("src/deep/nested/config.cs", """
            line1
            line2
            var key = "AKIAI44QH8DHBKUPLDRE";
            """);

        var result = await ScanAsync();

        var finding = Find(result, "secret", "AWS");
        finding.ShouldNotBeNull();
        finding.FilePath.ShouldBe("src/deep/nested/config.cs");
        finding.LineNumber.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EMPTY REPO
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmptyRepoReturnsCleanScore()
    {
        // No files added
        var result = await ScanAsync();

        result.SecurityScore.ShouldBe(10.0);
        result.Findings.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STUB
    // ═══════════════════════════════════════════════════════════════════

    private class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());

        private class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
