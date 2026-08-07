using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeGraph.Data;
using CodeGraph.Mcp.Hub;
using CodeGraph.Models.Responses;
using CodeGraph.Services.Query;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using PdfSharp.Pdf;
using System.Security.Claims;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Shouldly;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace CodeGraph.Tests.Services;

public sealed class ShortcutUploadStagingAreaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"codegraph-upload-tests-{Guid.NewGuid():N}");

    [Fact]
    public void StageAndOpen_UsesOwnerBoundSingleUseOpaqueHandle()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var content = Encoding.UTF8.GetBytes("review evidence");

        var staged = staging.Stage("token:41", "evidence.md", Convert.ToBase64String(content));

        staged.Handle.Length.ShouldBe(64);
        staged.Handle.ShouldNotContain("evidence");
        Directory.GetFiles(staging.SessionRoot).ShouldBeEmpty("staged bytes are captured immutably and the OS-backed temporary file is delete-on-close");
        Should.Throw<McpHubProviderPolicyException>(() => staging.Open("token:99", staged.Handle))
            .Message.ShouldContain("different caller");

        using (var lease = staging.Open("token:41", staged.Handle))
        {
            using var reader = new StreamReader(lease.Stream, leaveOpen: true);
            reader.ReadToEnd().ShouldBe("review evidence");
            lease.DisplayName.ShouldBe("evidence.md");
            lease.ContentType.ShouldBe("text/markdown");
            lease.Complete();
        }

        File.Exists(Path.Combine(staging.SessionRoot, staged.Handle)).ShouldBeFalse();
        Should.Throw<McpHubProviderPolicyException>(() => staging.Open("token:41", staged.Handle));
    }

    [Fact]
    public void Open_RejectsConcurrentUse_AndDisposeConsumesTheHandle()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var staged = staging.Stage("token:1", "safe.txt", Convert.ToBase64String("safe"u8.ToArray()));

        using var lease = staging.Open("token:1", staged.Handle);
        Should.Throw<McpHubProviderPolicyException>(() => staging.Open("token:1", staged.Handle))
            .Message.ShouldContain("in use");
        lease.Dispose();
        Should.Throw<McpHubProviderPolicyException>(() => staging.Open("token:1", staged.Handle))
            .Message.ShouldContain("invalid or expired");
    }

    [Fact]
    public void Open_RejectsAndDeletesExpiredHandle()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        using var staging = new ShortcutUploadStagingArea(root, clock);
        var staged = staging.Stage("token:1", "safe.txt", Convert.ToBase64String("safe"u8.ToArray()));
        clock.Advance(TimeSpan.FromMinutes(16));

        Should.Throw<McpHubProviderPolicyException>(() => staging.Open("token:1", staged.Handle))
            .Message.ShouldContain("invalid or expired");
        File.Exists(Path.Combine(staging.SessionRoot, staged.Handle)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("C:\\Windows\\secret.txt")]
    [InlineData("\\\\server\\share\\secret.txt")]
    [InlineData("safe.txt:stream")]
    public void Stage_RejectsTraversalRootedAndMixedSeparatorNames(string displayName)
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);

        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", displayName, Convert.ToBase64String("content"u8.ToArray())));
    }

    [Fact]
    public void Stage_RejectsOversizedDisallowedAndMismatchedContent()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);

        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "payload.exe", Convert.ToBase64String("MZ"u8.ToArray())))
            .Message.ShouldContain("not allowed");
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "fake.png", Convert.ToBase64String("not a png"u8.ToArray())))
            .Message.ShouldContain("does not match");

        var oversizedBase64 = new string('A', ((ShortcutUploadStagingArea.MaxFileBytes + 2) / 3) * 4 + 12);
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "large.txt", oversizedBase64))
            .Message.ShouldContain("may not exceed");
    }

    [Fact]
    public void Stage_RejectsSymlinkCollisionWithoutWritingOutsideTheStagingRoot()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows symbolic-link creation needs an optional host privilege.

        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(outside, "secret");
        staging.BeforeStageFileCreateForTest = path => File.CreateSymbolicLink(path, outside);

        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "safe.txt", Convert.ToBase64String("safe"u8.ToArray())))
            .Message.ShouldContain("captured safely");
        File.ReadAllText(outside).ShouldBe("secret");
        Directory.GetFileSystemEntries(staging.SessionRoot).ShouldHaveSingleItem("a failed CreateNew must not unlink the colliding path");
    }

    [Fact]
    public void Open_UsesImmutableCapturedBytes_AndNeverDeletesAnAttackerCreatedHandlePath()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var staged = staging.Stage("token:1", "safe.txt", Convert.ToBase64String("safe"u8.ToArray()));
        var path = Path.Combine(staging.SessionRoot, staged.Handle);
        staging.BeforeUploadLeaseForTest = candidate => File.WriteAllText(candidate, "host");

        using (var lease = staging.Open("token:1", staged.Handle))
        using (var reader = new StreamReader(lease.Stream, leaveOpen: true))
            reader.ReadToEnd().ShouldBe("safe");
        File.ReadAllText(path).ShouldBe("host", "cleanup must never unlink a same-account process's path replacement");
    }

    [Fact]
    public void Stage_ParsesDeclaredStructuredFormats_AndValidatesOfficePackageIdentity()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);

        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "bad.json", Convert.ToBase64String("not-json"u8.ToArray())));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "bad.yaml", Convert.ToBase64String("key: [unterminated"u8.ToArray())));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "bad.csv", Convert.ToBase64String("a,\"unterminated"u8.ToArray())));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "fake.docx", Convert.ToBase64String(CreateZip(("payload.bin", "host data")))));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "wrong.xlsx", Convert.ToBase64String(CreateOfficePackage(OfficeKind.Word))));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "missing-relationship.docx", Convert.ToBase64String(CreateSpoofOfficePackage(
                "word/document.xml", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", includeRootRelationship: false))));
        Should.Throw<McpHubProviderPolicyException>(() =>
            staging.Stage("token:1", "wrong-root.docx", Convert.ToBase64String(CreateSpoofOfficePackage(
                "word/document.xml", "urn:not-wordprocessing", includeRootRelationship: true))));

        foreach (var kind in Enum.GetValues<OfficeKind>())
        {
            var valid = staging.Stage("token:1", $"valid.{OfficeExtension(kind)}", Convert.ToBase64String(CreateOfficePackage(kind)));
            using var lease = staging.Open("token:1", valid.Handle);
            lease.SizeBytes.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void Stage_FullyParsesPdfAndImageFormats_InsteadOfTrustingSpoofableEnvelopes()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);

        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "spoof.pdf", Convert.ToBase64String("%PDF-1.7\nnot objects\n%%EOF"u8.ToArray())));
        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "spoof.png", Convert.ToBase64String(CreateSpoofPngEnvelope())));
        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "spoof.jpg", Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0x00, 0xFF, 0xD9])));
        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "spoof.gif", Convert.ToBase64String("GIF89a0000000;"u8.ToArray())));
        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "spoof.webp", Convert.ToBase64String(CreateSpoofWebpEnvelope())));

        using var image = new Image<Rgba32>(2, 2);
        using var png = new MemoryStream();
        image.SaveAsPng(png);
        Should.Throw<McpHubProviderPolicyException>(() => staging.Stage(
            "token:1", "wrong.jpg", Convert.ToBase64String(png.ToArray())));
        var validImage = staging.Stage("token:1", "valid.png", Convert.ToBase64String(png.ToArray()));
        using var imageLease = staging.Open("token:1", validImage.Handle);

        foreach (var (extension, encoded) in EncodeRemainingImageFormats(image))
        {
            var staged = staging.Stage("token:1", $"valid.{extension}", Convert.ToBase64String(encoded));
            using var lease = staging.Open("token:1", staged.Handle);
            lease.SizeBytes.ShouldBe(encoded.LongLength);
        }

        var validPdf = staging.Stage("token:1", "valid.pdf", Convert.ToBase64String(CreatePdf()));
        using var pdfLease = staging.Open("token:1", validPdf.Handle);
    }

    [Fact]
    public async Task ProviderFailure_PermanentlyConsumesUploadHandle()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var service = Service(
            ShortcutStore(),
            new StubHttpClientFactory(_ => throw new HttpRequestException("response lost after send")),
            staging);
        var json = await service.StageShortcutFileAsync(
            "alice", 17, "notes.md", Convert.ToBase64String("hello"u8.ToArray()));
        var handle = JsonDocument.Parse(json).RootElement.GetProperty("handle").GetString()!;

        await Should.ThrowAsync<HttpRequestException>(() =>
            service.UploadShortcutFileAsync("alice", 17, 123, handle));
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.UploadShortcutFileAsync("alice", 17, 123, handle));
    }

    [Fact]
    public void Constructor_NeverScavengesForgeablePriorSessionPaths()
    {
        var now = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var foreignRoot = Path.Combine(root, "session-00000000000000000000000000000000");
        Directory.CreateDirectory(foreignRoot);
        File.WriteAllText(Path.Combine(foreignRoot, ".codegraph-shortcut-upload-v1"), "CodeGraph Shortcut upload staging v1\n");
        var forgedHandle = new string('a', 64);
        File.WriteAllText(Path.Combine(foreignRoot, forgedHandle), "owned elsewhere");

        using var nextProcess = new ShortcutUploadStagingArea(root, clock);

        Directory.Exists(foreignRoot).ShouldBeTrue();
        File.ReadAllText(Path.Combine(foreignRoot, forgedHandle)).ShouldBe("owned elsewhere");
    }

    [Fact]
    public void Constructor_NeverFollowsForgedPriorSessionDirectoryLinks()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows directory links need an optional host privilege.

        Directory.CreateDirectory(root);
        var foreignRoot = Path.Combine(root, "foreign");
        Directory.CreateDirectory(foreignRoot);
        var sentinel = Path.Combine(foreignRoot, "sentinel.txt");
        File.WriteAllText(sentinel, "must survive");
        var forgedSession = Path.Combine(root, "session-11111111111111111111111111111111");
        Directory.CreateSymbolicLink(forgedSession, foreignRoot);

        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);

        File.ReadAllText(sentinel).ShouldBe("must survive");
        new DirectoryInfo(forgedSession).LinkTarget.ShouldNotBeNull();
    }

    [Fact]
    public void Constructor_RejectsConfiguredSymlinkRoot()
    {
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(root);
        var physical = Path.Combine(root, "physical");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(physical);
        Directory.CreateSymbolicLink(link, physical);

        Should.Throw<McpHubProviderPolicyException>(() =>
            new ShortcutUploadStagingArea(link, TimeProvider.System));
    }

    [Fact]
    public async Task Service_UploadsOnlyStagedHandle_WithExpectedMultipartMetadata()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        HttpRequestMessage? captured = null;
        byte[]? uploaded = null;
        string? disposition = null;
        var factory = new StubHttpClientFactory(request =>
        {
            captured = request;
            var part = ((MultipartFormDataContent)request.Content!)
                .Single(item => item.Headers.ContentDisposition?.Name == "file0");
            uploaded = part.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            disposition = part.Headers.ContentDisposition?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        });
        var store = ShortcutStore();
        var service = Service(store, factory, staging);

        var json = await service.StageShortcutFileAsync(
            "alice", 17, "notes.md", Convert.ToBase64String("hello"u8.ToArray()));
        var handle = JsonDocument.Parse(json).RootElement.GetProperty("handle").GetString()!;

        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.UploadShortcutFileAsync("alice", 18, 123, handle));
        var result = await service.UploadShortcutFileAsync("alice", 17, 123, handle);

        result.ShouldBe("{\"ok\":true}");
        captured!.RequestUri!.ToString().ShouldBe("https://api.app.shortcut.com/api/v3/files");
        uploaded.ShouldBe("hello"u8.ToArray());
        disposition.ShouldContain("notes.md");
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.UploadShortcutFileAsync("alice", 17, 123, handle));
        await Should.ThrowAsync<McpHubProviderPolicyException>(() =>
            service.UploadShortcutFileAsync("alice", 17, 123, "/etc/passwd"));
    }

    [Fact]
    public async Task LegacyAllEntitlement_ExcludesStagingAndUploadUntilExplicitlySelected()
    {
        var store = new InMemoryMcpHubStore();

        (await store.IsTokenEntitledAsync(7, "search_graph")).ShouldBeTrue();
        (await store.IsTokenEntitledAsync(7, "stories-stage-file")).ShouldBeFalse();
        (await store.IsTokenEntitledAsync(7, "stories-upload-file")).ShouldBeFalse();

        await store.ReplaceTokenEntitlementsAsync(7, ["stories-stage-file", "stories-upload-file"]);
        (await store.IsTokenEntitledAsync(7, "stories-stage-file")).ShouldBeTrue();
        (await store.IsTokenEntitledAsync(7, "stories-upload-file")).ShouldBeTrue();
        (await store.IsTokenEntitledAsync(7, "search_graph")).ShouldBeFalse();
    }

    [Fact]
    public async Task Server_AuditsOwnerAndOpaqueHandleAcrossStageAndUpload()
    {
        using var staging = new ShortcutUploadStagingArea(root, TimeProvider.System);
        var store = ShortcutStore();
        var service = Service(
            store,
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            }),
            staging);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("preferred_username", "Alice"),
                new Claim("mcp_pat_token_id", "17"),
            ], "test"))
        };
        var server = new McpHubServer(service, new HttpContextAccessor { HttpContext = context });

        var stageJson = await server.StageShortcutStoryFile(
            "notes.md", Convert.ToBase64String("hello"u8.ToArray()));
        var handle = JsonDocument.Parse(stageJson).RootElement.GetProperty("handle").GetString()!;
        await server.UploadShortcutStoryFile(123, handle);

        store.Audit.Count.ShouldBe(2);
        store.Audit[0].Username.ShouldBe("alice");
        store.Audit[0].TokenId.ShouldBe(17);
        store.Audit[0].ToolName.ShouldBe("stories-stage-file");
        store.Audit[0].ResourceKey.ShouldBe($"staged:{handle}");
        store.Audit[1].ToolName.ShouldBe("stories-upload-file");
        store.Audit[1].ResourceKey.ShouldBe($"story:123/staged:{handle}");
        store.Audit.ShouldAllBe(item => item.Success && item.AuthorizationDecision == "allowed");
    }

    private static RecordingStore ShortcutStore()
    {
        var store = new RecordingStore
        {
            Providers = [new() { ProviderKey = "shortcut", Enabled = true }]
        };
        store.Credentials[("shortcut", "apiToken")] = "token";
        return store;
    }

    private static byte[] CreateOfficePackage(OfficeKind kind)
    {
        using var output = new MemoryStream();
        switch (kind)
        {
            case OfficeKind.Word:
                using (var package = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, autoSave: true))
                {
                    var main = package.AddMainDocumentPart();
                    main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("valid")))));
                }
                break;
            case OfficeKind.Spreadsheet:
                using (var package = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, autoSave: true))
                {
                    var workbook = package.AddWorkbookPart();
                    workbook.Workbook = new S.Workbook();
                    var worksheet = workbook.AddNewPart<WorksheetPart>();
                    worksheet.Worksheet = new S.Worksheet(new S.SheetData());
                    workbook.Workbook.Append(new S.Sheets(new S.Sheet
                    {
                        Id = workbook.GetIdOfPart(worksheet),
                        SheetId = 1,
                        Name = "Sheet1",
                    }));
                }
                break;
            case OfficeKind.Presentation:
                using (var package = PresentationDocument.Create(output, PresentationDocumentType.Presentation, autoSave: true))
                {
                    var presentation = package.AddPresentationPart();
                    var master = presentation.AddNewPart<SlideMasterPart>("rId1");
                    var layout = master.AddNewPart<SlideLayoutPart>("rId1");
                    layout.SlideLayout = new P.SlideLayout(
                        new P.CommonSlideData(CreateEmptyShapeTree()),
                        new P.ColorMapOverride(new D.MasterColorMapping()));
                    master.SlideMaster = new P.SlideMaster(
                        new P.CommonSlideData(CreateEmptyShapeTree()),
                        new P.ColorMap
                        {
                            Background1 = D.ColorSchemeIndexValues.Light1,
                            Text1 = D.ColorSchemeIndexValues.Dark1,
                            Background2 = D.ColorSchemeIndexValues.Light2,
                            Text2 = D.ColorSchemeIndexValues.Dark2,
                            Accent1 = D.ColorSchemeIndexValues.Accent1,
                            Accent2 = D.ColorSchemeIndexValues.Accent2,
                            Accent3 = D.ColorSchemeIndexValues.Accent3,
                            Accent4 = D.ColorSchemeIndexValues.Accent4,
                            Accent5 = D.ColorSchemeIndexValues.Accent5,
                            Accent6 = D.ColorSchemeIndexValues.Accent6,
                            Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
                            FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
                        },
                        new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2_147_483_649U, RelationshipId = "rId1" }),
                        new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
                    layout.AddPart(master, "rId1");
                    presentation.Presentation = new P.Presentation(
                        new P.SlideMasterIdList(new P.SlideMasterId { Id = 2_147_483_648U, RelationshipId = "rId1" }),
                        new P.SlideSize { Cx = 9_144_000, Cy = 6_858_000 },
                        new P.NotesSize { Cx = 6_858_000, Cy = 9_144_000 },
                        new P.DefaultTextStyle());
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return output.ToArray();
    }

    private static P.ShapeTree CreateEmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new D.TransformGroup()));

    private static byte[] CreateSpoofOfficePackage(
        string mainPart,
        string primaryNamespace,
        bool includeRootRelationship)
    {
        var contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
        var files = new List<(string Name, string Content)>
        {
            ("[Content_Types].xml", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Override PartName="/{mainPart}" ContentType="{contentType}" />
                </Types>
                """),
            (mainPart, $"<document xmlns=\"{primaryNamespace}\"><body /></document>"),
        };
        if (includeRootRelationship)
        {
            files.Add(("_rels/.rels", $"""
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{mainPart}" />
                </Relationships>
                """));
        }
        return CreateZip(files.ToArray());
    }

    private static string OfficeExtension(OfficeKind kind) => kind switch
    {
        OfficeKind.Word => "docx",
        OfficeKind.Spreadsheet => "xlsx",
        OfficeKind.Presentation => "pptx",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] CreatePdf()
    {
        using var output = new MemoryStream();
        using var document = new PdfDocument();
        document.AddPage();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static IReadOnlyList<(string Extension, byte[] Content)> EncodeRemainingImageFormats(Image<Rgba32> image)
    {
        var result = new List<(string, byte[])>();
        foreach (var extension in new[] { "jpg", "gif", "webp" })
        {
            using var output = new MemoryStream();
            switch (extension)
            {
                case "jpg": image.SaveAsJpeg(output); break;
                case "gif": image.SaveAsGif(output); break;
                case "webp": image.SaveAsWebp(output); break;
            }
            result.Add((extension, output.ToArray()));
        }
        return result;
    }

    private static byte[] CreateSpoofPngEnvelope()
    {
        var bytes = new byte[45];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        "IEND"u8.CopyTo(bytes.AsSpan(bytes.Length - 8));
        return bytes;
    }

    private static byte[] CreateSpoofWebpEnvelope()
    {
        var bytes = new byte[20];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.GetBytes((uint)12).CopyTo(bytes, 4);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }

    private enum OfficeKind { Word, Spreadsheet, Presentation }

    private static McpHubService Service(
        RecordingStore store,
        IHttpClientFactory factory,
        ShortcutUploadStagingArea staging)
    {
        var provider = new ServiceCollection()
            .AddSingleton<IMcpSensitiveColumnStore>(new InMemoryMcpSensitiveColumnStore())
            .AddSingleton<IDatabaseSourceStore>(new EmptyDatabaseSourceStore())
            .BuildServiceProvider();
        return new McpHubService(
            store,
            new EmptyProjectQueryService(),
            factory,
            new SensitiveColumnPolicy(provider.GetRequiredService<IServiceScopeFactory>()),
            new MySqlSourceExposurePolicy(provider.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<McpHubService>.Instance,
            staging);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(responder)) { BaseAddress = new Uri("https://api.app.shortcut.com/api/v3/") };

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(responder(request));
        }
    }

    private sealed class RecordingStore : IMcpHubStore
    {
        public List<McpHubProviderEntity> Providers { get; init; } = [];
        public Dictionary<(string Provider, string Key), string?> Credentials { get; } = [];
        public List<McpHubAuditEntity> Audit { get; } = [];
        public Task<IReadOnlyList<McpHubProviderEntity>> ListProvidersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubProviderEntity>>(Providers);
        public Task<string?> GetCredentialValueAsync(string providerKey, string credentialKey, CancellationToken ct = default) => Task.FromResult(Credentials.GetValueOrDefault((providerKey, credentialKey)));
        public Task<IReadOnlyList<McpHubToolEntity>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubToolEntity>>([]);
        public Task UpsertProviderAsync(McpHubProviderEntity provider, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertToolAsync(McpHubToolEntity tool, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SetProviderEnabledAsync(string providerKey, bool enabled, bool? sourceVisible, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> UpdateToolCatalogStateAsync(string toolName, bool? enabled, bool? defaultSelected, string? accessClass, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubCredentialEntity>> ListCredentialsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetCredentialValueAsync(string providerKey, string credentialKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpHubConfigEntity>> ListConfigAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetConfigValueAsync(string providerKey, string configKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetConfigValueAsync(string providerKey, string configKey, string? value, string? updatedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceTokenEntitlementsAsync(long tokenId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetTokenEntitlementsAsync(long tokenId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsTokenEntitledAsync(long tokenId, string toolName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateAuditAsync(McpHubAuditEntity audit, CancellationToken ct = default)
        {
            Audit.Add(audit);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<McpHubAuditEntity>> ListAuditAsync(int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<McpHubAuditEntity>>(Audit);
    }

    private sealed class EmptyDatabaseSourceStore : IDatabaseSourceStore
    {
        public Task<IReadOnlyList<DatabaseSourceEntity>> ListAsync() => Task.FromResult<IReadOnlyList<DatabaseSourceEntity>>([]);
        public Task<DatabaseSourceEntity?> GetAsync(long id) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity> CreateAsync(DatabaseSourceEntity entity) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateAsync(long id, string? serverName, string? databaseName, string? connectionString, bool? enabled) => throw new NotSupportedException();
        public Task<DatabaseSourceEntity?> UpdateMcpExposureAsync(long id, bool? mcpHubEnabled, string? mcpExposureMode, string? mcpDisplayName, string? mcpEnvironment) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(long id) => throw new NotSupportedException();
        public Task UpdateLastSyncedAsync(long id) => Task.CompletedTask;
    }

    private sealed class EmptyProjectQueryService : IProjectQueryService
    {
        public Task<ProjectListResponse> ListAsync(string? search, string? group, int page, int pageSize) => throw new NotSupportedException();
        public Task<SchemaListResponse> ListSchemasAsync(string? search, string? server, string? database, int page, int pageSize) => throw new NotSupportedException();
        public Task<SchemaCatalogResponse?> GetSchemaCatalogAsync(string name) => throw new NotSupportedException();
        public Task<ProjectDetailResponse?> GetDetailAsync(string name) => throw new NotSupportedException();
        public Task<ProjectHealthResponse?> GetHealthAsync(string name) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileMetrics>> GetMetricsAsync(string name, string? dotnetProject, int top) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileMetrics>> GetHotspotsAsync(string name, int top) => throw new NotSupportedException();
        public Task<NodeListResponse> GetNodesAsync(string name, string? label, string? dotnetProject, int page, int pageSize) => throw new NotSupportedException();
        public Task<AnalysisBatchResponse?> GetBatchStatusAsync(string name) => throw new NotSupportedException();
        public Task<ProjectSecurityResponse?> GetSecurityAsync(string name) => throw new NotSupportedException();
        public Task<string?> GetReadmeAsync(string name) => throw new NotSupportedException();
    }
}
