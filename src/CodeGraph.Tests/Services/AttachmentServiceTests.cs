using System.Text;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        public Task DeleteAttachmentAsync(WikiAttachmentEntity entity)
        {
            Attachments.Remove(entity);
            return Task.CompletedTask;
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
}
