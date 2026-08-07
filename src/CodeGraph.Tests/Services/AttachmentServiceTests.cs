using System.Text;
using System.Text.Json;
using System.Diagnostics;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class AttachmentServiceTests : IDisposable
{
    private readonly string storageRoot = Path.Combine(
        Path.GetTempPath(), $"codegraph-attachment-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("/tmp/report.txt", "report.txt")]
    [InlineData("../../report.txt", "report.txt")]
    [InlineData("C:\\temp\\report.txt", "report.txt")]
    [InlineData("../mixed\\path/report.txt", "report.txt")]
    public async Task UploadAsync_SafelyRenamesUntrustedDisplayNamesAndStoresOpaqueFiles(
        string suppliedName,
        string expectedDisplayName)
    {
        var store = new InMemoryWikiStore();
        var service = CreateService(store);

        var response = await service.UploadAsync(
            1,
            suppliedName,
            "text/plain",
            new MemoryStream("safe content"u8.ToArray()),
            "codex");

        response.ShouldNotBeNull();
        response.Filename.ShouldBe(expectedDisplayName);
        var entity = store.Attachments.Single();
        entity.Filename.ShouldBe(expectedDisplayName);
        Path.GetFileName(entity.StoragePath).ShouldNotBe(expectedDisplayName);
        IsUnderRoot(entity.StoragePath, storageRoot).ShouldBeTrue();
        (await File.ReadAllTextAsync(entity.StoragePath)).ShouldBe("safe content");
    }

    [Fact]
    public async Task UploadAsync_DuplicateDisplayNamesCreateDistinctFilesWithoutOverwriting()
    {
        var store = new InMemoryWikiStore();
        var service = CreateService(store);

        var first = await service.UploadAsync(
            1, "report.txt", "text/plain", new MemoryStream("first"u8.ToArray()), "codex");
        var second = await service.UploadAsync(
            1, "report.txt", "text/plain", new MemoryStream("second"u8.ToArray()), "codex");

        first!.Filename.ShouldBe("report.txt");
        second!.Filename.ShouldBe("report.txt");
        store.Attachments.Count.ShouldBe(2);
        store.Attachments[0].StoragePath.ShouldNotBe(store.Attachments[1].StoragePath);
        (await File.ReadAllTextAsync(store.Attachments[0].StoragePath)).ShouldBe("first");
        (await File.ReadAllTextAsync(store.Attachments[1].StoragePath)).ShouldBe("second");
    }

    [Fact]
    public async Task UploadAsync_RejectsSymlinkedPageDirectory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var outside = Path.Combine(Path.GetTempPath(), $"codegraph-attachment-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(storageRoot, "1"), outside);
            var service = CreateService(new InMemoryWikiStore());

            await Should.ThrowAsync<IOException>(() => service.UploadAsync(
                1, "report.txt", "text/plain", new MemoryStream("safe"u8.ToArray()), "codex"));

            Directory.EnumerateFileSystemEntries(outside).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(Path.Combine(storageRoot, "1"));
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task StorageRoot_ResolvesAndPinsSymlinkedAncestor()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var testDirectory = Path.Combine(Path.GetTempPath(), $"codegraph-root-pin-{Guid.NewGuid():N}");
        var firstTarget = Path.Combine(testDirectory, "first");
        var secondTarget = Path.Combine(testDirectory, "second");
        var link = Path.Combine(testDirectory, "current");
        Directory.CreateDirectory(firstTarget);
        Directory.CreateDirectory(secondTarget);
        Directory.CreateSymbolicLink(link, firstTarget);
        try
        {
            var store = new InMemoryWikiStore();
            var service = new AttachmentService(
                store,
                Options.Create(new WikiOptions { AttachmentStoragePath = Path.Combine(link, "attachments") }),
                NullLogger<AttachmentService>.Instance);

            await service.UploadAsync(
                1, "first.txt", "text/plain", new MemoryStream("first"u8.ToArray()), "codex");

            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, secondTarget);
            await service.UploadAsync(
                1, "second.txt", "text/plain", new MemoryStream("second"u8.ToArray()), "codex");

            Directory.EnumerateFiles(Path.Combine(firstTarget, "attachments", "1")).Count().ShouldBe(2);
            Directory.Exists(Path.Combine(secondTarget, "attachments")).ShouldBeFalse();
            await using var second = (await service.GetAsync(store.Attachments[1].Id))!.Value.Content;
            using var reader = new StreamReader(second);
            (await reader.ReadToEndAsync()).ShouldBe("second");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAndRead_RejectPageDirectorySwapToSymlink()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var outside = Path.Combine(Path.GetTempPath(), $"codegraph-swap-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var store = new InMemoryWikiStore();
            var service = CreateService(store);
            await service.UploadAsync(
                1, "first.txt", "text/plain", new MemoryStream("first"u8.ToArray()), "codex");
            var pageDirectory = Path.Combine(storageRoot, "1");
            var displacedDirectory = Path.Combine(storageRoot, "displaced");
            Directory.Move(pageDirectory, displacedDirectory);
            Directory.CreateSymbolicLink(pageDirectory, outside);

            await Should.ThrowAsync<IOException>(() => service.UploadAsync(
                1, "second.txt", "text/plain", new MemoryStream("second"u8.ToArray()), "codex"));
            (await service.GetAsync(store.Attachments[0].Id)).ShouldBeNull();
            Directory.EnumerateFileSystemEntries(outside).ShouldBeEmpty();

            Directory.Delete(pageDirectory);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task GetAndDeleteAsync_RejectPathsOutsideStorageRoot()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"codegraph-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "do not touch");
        try
        {
            var store = new InMemoryWikiStore();
            store.AddAttachment(new WikiAttachmentEntity
            {
                Id = 42,
                PageId = 1,
                Filename = "outside.txt",
                StoragePath = outsidePath,
                ContentType = "text/plain"
            });
            var service = CreateService(store);

            (await service.GetAsync(42)).ShouldBeNull();
            (await service.DeleteAsync(42)).ShouldBeFalse();
            File.Exists(outsidePath).ShouldBeTrue();
            store.Attachments.ShouldContain(a => a.Id == 42);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task GetAndDeleteAsync_RejectSymlinkedFiles()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var pageDirectory = Path.Combine(storageRoot, "1");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"codegraph-outside-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(pageDirectory);
        await File.WriteAllTextAsync(outsidePath, "do not touch");
        var linkPath = Path.Combine(pageDirectory, "opaque");
        File.CreateSymbolicLink(linkPath, outsidePath);
        try
        {
            var store = new InMemoryWikiStore();
            store.AddAttachment(new WikiAttachmentEntity
            {
                Id = 42,
                PageId = 1,
                Filename = "outside.txt",
                StoragePath = linkPath,
                ContentType = "text/plain"
            });
            var service = CreateService(store);

            (await service.GetAsync(42)).ShouldBeNull();
            (await service.DeleteAsync(42)).ShouldBeFalse();
            (await File.ReadAllTextAsync(outsidePath)).ShouldBe("do not touch");
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task UploadAsync_RemovesCreatedFileWhenMetadataPersistenceFails()
    {
        var store = new InMemoryWikiStore { FailAttachmentCreation = true };
        var service = CreateService(store);

        await Should.ThrowAsync<InvalidOperationException>(() => service.UploadAsync(
            1, "report.txt", "text/plain", new MemoryStream("safe"u8.ToArray()), "codex"));

        Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task UploadAsync_RemovesPartialFileWhenContentCopyFails()
    {
        var service = CreateService(new InMemoryWikiStore());

        await Should.ThrowAsync<IOException>(() => service.UploadAsync(
            1, "report.txt", "text/plain", new FailingReadStream(), "codex"));

        Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_RestoresFileWhenMetadataDeletionFails()
    {
        var store = new InMemoryWikiStore { FailAttachmentDeletion = true };
        var service = CreateService(store);
        var uploaded = await service.UploadAsync(
                           1, "report.txt", "text/plain", new MemoryStream("still here"u8.ToArray()), "codex")
                       ?? throw new InvalidOperationException("Attachment upload failed.");
        var storagePath = store.Attachments.Single().StoragePath;

        await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteAsync(uploaded.Id));

        store.Attachments.ShouldContain(a => a.Id == uploaded.Id);
        File.Exists(storagePath).ShouldBeTrue();
        Directory.EnumerateFiles(Path.GetDirectoryName(storagePath)!, ".delete-*").ShouldBeEmpty();
        var retrieved = await service.GetAsync(uploaded.Id)
                        ?? throw new InvalidOperationException("Restored attachment could not be read.");
        await using var content = retrieved.Content;
        using var reader = new StreamReader(content);
        (await reader.ReadToEndAsync()).ShouldBe("still here");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_WindowsJunctionSwapCannotRedirectCommitOrRollback(
        bool failMetadataDeletion)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(
            Path.GetTempPath(), $"codegraph-junction-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var pageDirectory = Path.Combine(storageRoot, "1");
        var displacedDirectory = Path.Combine(storageRoot, "displaced");
        var store = new InMemoryWikiStore { FailAttachmentDeletion = failMetadataDeletion };
        var service = CreateService(store);
        var uploaded = await service.UploadAsync(
                           1, "report.txt", "text/plain",
                           new MemoryStream("original"u8.ToArray()), "codex")
                       ?? throw new InvalidOperationException("Attachment upload failed.");
        var storagePath = store.Attachments.Single().StoragePath;
        File.Exists(storagePath).ShouldBeTrue(
            "the swap test must start with a durable successful upload");
        var storageName = Path.GetFileName(storagePath);
        var outsideFile = Path.Combine(outside, storageName);
        await File.WriteAllTextAsync(outsideFile, "outside sentinel");

        store.BeforeAttachmentDeletion = () =>
        {
            Directory.Move(pageDirectory, displacedDirectory);
            CreateDirectoryJunction(pageDirectory, outside);
            return Task.CompletedTask;
        };

        try
        {
            if (failMetadataDeletion)
                await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteAsync(uploaded.Id));
            else
                (await service.DeleteAsync(uploaded.Id)).ShouldBeTrue();

            (await File.ReadAllTextAsync(outsideFile)).ShouldBe("outside sentinel");
            var displacedFile = Path.Combine(displacedDirectory, storageName);
            File.Exists(displacedFile).ShouldBe(failMetadataDeletion);
            store.Attachments.Any(a => a.Id == uploaded.Id).ShouldBe(failMetadataDeletion);
            if (failMetadataDeletion)
                (await File.ReadAllTextAsync(displacedFile)).ShouldBe("original");
        }
        finally
        {
            if (File.Exists(outsideFile))
                File.Delete(outsideFile);
            if (Directory.Exists(pageDirectory))
                Directory.Delete(pageDirectory);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_WindowsFileAndPageSwapCannotRedirectOpenHandleWrite()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(
            Path.GetTempPath(), $"codegraph-upload-swap-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var pageDirectory = Path.Combine(storageRoot, "1");
        var displacedDirectory = Path.Combine(storageRoot, "displaced");
        Exception? fileMoveError = null;
        Exception? pageSwapError = null;
        var content = new BeforeFirstReadStream("original"u8.ToArray(), () =>
        {
            try
            {
                var createdFile = Directory.EnumerateFiles(pageDirectory).Single();
                File.Move(createdFile, Path.Combine(outside, Path.GetFileName(createdFile)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                fileMoveError = ex;
            }

            try
            {
                Directory.Move(pageDirectory, displacedDirectory);
                CreateDirectoryJunction(pageDirectory, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pageSwapError = ex;
            }
        });

        try
        {
            var store = new InMemoryWikiStore();
            var service = CreateService(store);
            await service.UploadAsync(1, "report.txt", "text/plain", content, "codex");

            fileMoveError.ShouldNotBeNull("the exclusive file handle must prevent target substitution");
            pageSwapError.ShouldNotBeNull("the open page handle must prevent a junction swap");
            Directory.EnumerateFileSystemEntries(outside).ShouldBeEmpty();
            var storagePath = store.Attachments.Single().StoragePath;
            File.Exists(storagePath).ShouldBeTrue();
            (await File.ReadAllTextAsync(storagePath)).ShouldBe("original");
        }
        finally
        {
            if (Directory.Exists(pageDirectory) &&
                (File.GetAttributes(pageDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(pageDirectory);
            }
            if (Directory.Exists(displacedDirectory))
                Directory.Delete(displacedDirectory, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task WikiController_UploadDownloadAndDelete_RoundTripsAttachment()
    {
        var store = new InMemoryWikiStore();
        var options = Options.Create(new WikiOptions { AttachmentStoragePath = storageRoot });
        var attachments = new AttachmentService(store, options, NullLogger<AttachmentService>.Instance);
        var controller = new WikiController(new SinglePageWikiService(), attachments, options);
        var bytes = Encoding.UTF8.GetBytes("controller content");
        var formFiles = new FormFileCollection
        {
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "../report.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            }
        };
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=codegraph-test";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>(),
            formFiles);
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var uploadResult = await controller.CreateOrUpload("docs", "guide/attachments");
        var uploaded = uploadResult.ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<WikiAttachmentResponse>();
        uploaded.Filename.ShouldBe("report.txt");
        var storedPath = store.Attachments.Single().StoragePath;

        var downloadResult = await controller.DownloadAttachment(uploaded.Id, uploaded.Filename);
        var fileResult = downloadResult.ShouldBeOfType<FileStreamResult>();
        await using (fileResult.FileStream)
        using (var reader = new StreamReader(fileResult.FileStream, Encoding.UTF8))
            (await reader.ReadToEndAsync()).ShouldBe("controller content");
        fileResult.FileDownloadName.ShouldBe("report.txt");

        (await controller.DeleteAttachment(uploaded.Id)).ShouldBeOfType<NoContentResult>();
        File.Exists(storedPath).ShouldBeFalse();
        store.Attachments.ShouldBeEmpty();
        (await controller.DownloadAttachment(uploaded.Id, uploaded.Filename)).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task WikiAttachmentRoutes_RoundTripMultipartAndEncodedDownloadOverHttp()
    {
        var store = new InMemoryWikiStore();
        var options = Options.Create(new WikiOptions { AttachmentStoragePath = storageRoot });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IWikiStore>(store);
                    services.AddSingleton<IWikiService>(new SinglePageWikiService());
                    services.AddSingleton<IOptions<WikiOptions>>(options);
                    services.AddSingleton<IAttachmentService, AttachmentService>();
                    services.AddControllers().AddApplicationPart(typeof(WikiController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync(timeout.Token);
        using var client = host.GetTestClient();
        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent("http content"u8.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        multipart.Add(content, "file", "../quarterly report #1.txt");

        using var uploadResponse = await client.PostAsync(
                "/api/wiki/docs/guide/attachments", multipart, timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        uploadResponse.EnsureSuccessStatusCode();
        var uploaded = JsonSerializer.Deserialize<WikiAttachmentResponse>(
            await uploadResponse.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        uploaded.ShouldNotBeNull();
        uploaded.Filename.ShouldBe("quarterly report #1.txt");
        uploaded.DownloadUrl.ShouldEndWith("/quarterly%20report%20%231.txt");

        using var downloadResponse = await client.GetAsync(uploaded.DownloadUrl, timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        downloadResponse.EnsureSuccessStatusCode();
        (await downloadResponse.Content.ReadAsStringAsync()).ShouldBe("http content");
        var contentDisposition = downloadResponse.Content.Headers.ContentDisposition;
        contentDisposition.ShouldNotBeNull();
        contentDisposition.DispositionType.ShouldBe("attachment");
        contentDisposition.FileName.ShouldNotBeNull();
        contentDisposition.FileName.ShouldContain("quarterly report #1.txt");

        using var deleteResponse = await client.DeleteAsync(
                $"/api/wiki/attachments/{uploaded.Id}", timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        deleteResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        using var missingResponse = await client.GetAsync(uploaded.DownloadUrl, timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        missingResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    private AttachmentService CreateService(InMemoryWikiStore store) => new(
        store,
        Options.Create(new WikiOptions { AttachmentStoragePath = storageRoot }),
        NullLogger<AttachmentService>.Instance);

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start junction creation process.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create test junction: {process.StandardError.ReadToEnd()} " +
                process.StandardOutput.ReadToEnd());
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
    }

    private sealed class InMemoryWikiStore : IWikiStore
    {
        private long nextAttachmentId = 1;

        public List<WikiAttachmentEntity> Attachments { get; } = [];
        public bool FailAttachmentCreation { get; init; }
        public bool FailAttachmentDeletion { get; init; }
        public Func<Task>? BeforeAttachmentDeletion { get; set; }

        public void AddAttachment(WikiAttachmentEntity attachment)
        {
            Attachments.Add(attachment);
            nextAttachmentId = Math.Max(nextAttachmentId, attachment.Id + 1);
        }

        public Task<WikiPageEntity?> GetPageByIdAsync(long id) => Task.FromResult<WikiPageEntity?>(
            id == 1 ? new WikiPageEntity { Id = 1, Slug = "guide", Title = "Guide" } : null);

        public Task<IReadOnlyList<WikiAttachmentEntity>> ListAttachmentsAsync(long pageId) =>
            Task.FromResult<IReadOnlyList<WikiAttachmentEntity>>(
                Attachments.Where(a => a.PageId == pageId).ToList());

        public Task<WikiAttachmentEntity?> GetAttachmentByIdAsync(long id) =>
            Task.FromResult(Attachments.SingleOrDefault(a => a.Id == id));

        public Task<WikiAttachmentEntity> CreateAttachmentAsync(WikiAttachmentEntity entity)
        {
            if (FailAttachmentCreation)
                throw new InvalidOperationException("metadata write failed");
            entity.Id = nextAttachmentId++;
            Attachments.Add(entity);
            return Task.FromResult(entity);
        }

        public async Task DeleteAttachmentAsync(WikiAttachmentEntity entity)
        {
            if (BeforeAttachmentDeletion is not null)
                await BeforeAttachmentDeletion();
            if (FailAttachmentDeletion)
                throw new InvalidOperationException("metadata delete failed");
            Attachments.Remove(entity);
        }

        public Task<IReadOnlyList<WikiSectionEntity>> ListSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionEntity?> GetSectionBySlugAsync(string slug) => throw new NotSupportedException();
        public Task<WikiSectionEntity?> GetSectionByIdAsync(long id) => throw new NotSupportedException();
        public Task<int> CountSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionEntity> CreateSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task UpdateSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task DeleteSectionAsync(WikiSectionEntity entity) => throw new NotSupportedException();
        public Task<WikiPageEntity?> FindPageAsync(long sectionId, long? parentId, string slug) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiPageEntity>> GetPagesBySectionAsync(long sectionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiPageEntity>> GetAutoGeneratedPagesBySectionAsync(long sectionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiPageEntity>> SearchPagesAsync(long sectionId, string pattern) => throw new NotSupportedException();
        public Task<int> GetMaxSortOrderAsync(long sectionId, long? parentId) => throw new NotSupportedException();
        public Task<WikiPageEntity> CreatePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task UpdatePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task DeletePageAsync(WikiPageEntity entity) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiRevisionEntity>> GetRevisionsAsync(long pageId) => throw new NotSupportedException();
        public Task<WikiRevisionEntity?> GetRevisionAsync(long pageId, int revision) => throw new NotSupportedException();
        public Task CreateRevisionAsync(WikiRevisionEntity entity) => throw new NotSupportedException();
    }

    private sealed class SinglePageWikiService : IWikiService
    {
        public Task<WikiPageResponse?> GetPageAsync(string sectionSlug, string path) =>
            Task.FromResult<WikiPageResponse?>(path == "guide"
                ? new WikiPageResponse(
                    1, 1, null, "guide", "Guide", "content", null, "codex", 1, 0, 0,
                    false, false, DateTime.UtcNow, DateTime.UtcNow)
                : null);

        public Task<IReadOnlyList<WikiSectionResponse>> ListSectionsAsync() => throw new NotSupportedException();
        public Task<WikiSectionResponse?> GetSectionAsync(string sectionSlug) => throw new NotSupportedException();
        public Task<WikiSectionResponse?> CreateSectionAsync(WikiSectionRequest request) => throw new NotSupportedException();
        public Task<WikiSectionResponse?> UpdateSectionAsync(long id, WikiSectionRequest request) => throw new NotSupportedException();
        public Task<bool> DeleteSectionAsync(long id) => throw new NotSupportedException();
        public Task<List<WikiTreeNode>> GetSectionTreeAsync(string sectionSlug) => throw new NotSupportedException();
        public Task<WikiPageListItem?> CreatePageAsync(string sectionSlug, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<WikiPageListItem?> CreateChildPageAsync(string sectionSlug, string parentPath, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<WikiPageListItem?> UpdatePageAsync(string sectionSlug, string path, WikiPageRequest request, string author) => throw new NotSupportedException();
        public Task<bool> DeletePageAsync(string sectionSlug, string path) => throw new NotSupportedException();
        public Task<bool> MovePageAsync(string sectionSlug, string path, WikiPageMoveRequest request) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiRevisionListItem>> GetRevisionsAsync(string sectionSlug, string path) => throw new NotSupportedException();
        public Task<WikiRevisionResponse?> GetRevisionAsync(string sectionSlug, string path, int revision) => throw new NotSupportedException();
    }

    private sealed class FailingReadStream : Stream
    {
        private bool hasReturnedContent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (hasReturnedContent)
                throw new IOException("simulated upload interruption");

            hasReturnedContent = true;
            "partial"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult("partial"u8.Length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BeforeFirstReadStream(byte[] content, Action beforeFirstRead) : Stream
    {
        private int position;
        private bool invoked;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!invoked)
            {
                invoked = true;
                beforeFirstRead();
            }

            var count = Math.Min(buffer.Length, content.Length - position);
            content.AsSpan(position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
