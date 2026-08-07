using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using CodeGraph.Services;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using YamlDotNet.Core;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Process-local, isolated staging for outbound Shortcut attachments. Callers supply content,
/// never a host filesystem path; a random, owner-bound handle is the only upload capability.
/// </summary>
public sealed class ShortcutUploadStagingArea : IDisposable
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    private const long MaxStagedBytes = 64L * 1024 * 1024;
    private const long MaxExpandedOfficeBytes = 40L * 1024 * 1024;
    private const int MaxOfficeEntries = 4096;
    private const int MaxContentTypesBytes = 256 * 1024;
    private const long MaxAggregateImagePixels = 50_000_000;
    private const int MaxImageFrames = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly DecoderOptions ImageIdentificationOptions = new()
    {
        MaxFrames = MaxImageFrames + 1,
        SkipMetadata = true,
    };
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".json"] = "application/json",
            [".yaml"] = "application/yaml",
            [".yml"] = "application/yaml",
            [".csv"] = "text/csv",
            [".tsv"] = "text/tab-separated-values",
            [".log"] = "text/plain",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly AttachmentStorage storage;
    private readonly long sessionId;
    private readonly string sessionRoot;
    private readonly Timer cleanupTimer;
    private long reservedBytes;
    private int disposed;

    internal Action<string>? BeforeStageFileCreateForTest { get; set; }
    internal Action<string>? AfterStageFileCreateForTest { get; set; }
    internal Action<string>? BeforeUploadLeaseForTest { get; set; }

    public ShortcutUploadStagingArea()
        : this(rootPath: null, TimeProvider.System)
    {
    }

    internal ShortcutUploadStagingArea(string? rootPath, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        var stagingParent = CreatePrivateStagingParent(rootPath);
        storage = new AttachmentStorage(stagingParent);
        sessionId = CreateSessionId();
        storage.EnsureContainer(sessionId);
        sessionRoot = Path.Combine(
            stagingParent,
            sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cleanupTimer = new Timer(_ => MaintainSafely(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal string SessionRoot => sessionRoot;

    public async Task<StagedShortcutUpload> StageAsync(
        string ownerKey,
        string displayName,
        string base64Content,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        CleanupExpired();

        if (string.IsNullOrWhiteSpace(ownerKey))
            throw new McpHubProviderPolicyException("An authenticated staging owner is required.");

        var safeName = ValidateDisplayName(displayName);
        var extension = Path.GetExtension(safeName);
        if (!AllowedTypes.TryGetValue(extension, out var contentType))
            throw new McpHubProviderPolicyException($"Files of type '{extension}' are not allowed for Shortcut upload.");

        if (string.IsNullOrWhiteSpace(base64Content))
            throw new McpHubProviderPolicyException("base64Content is required.");

        var maxEncodedLength = ((MaxFileBytes + 2L) / 3L) * 4L + 8L;
        if (base64Content.Length > maxEncodedLength)
            throw new McpHubProviderPolicyException($"Shortcut attachments may not exceed {MaxFileBytes} bytes.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Content);
        }
        catch (FormatException)
        {
            throw new McpHubProviderPolicyException("base64Content is not valid base64.");
        }

        if (bytes.Length == 0 || bytes.Length > MaxFileBytes)
            throw new McpHubProviderPolicyException($"Shortcut attachments must contain 1 to {MaxFileBytes} bytes.");

        ValidateContent(extension, bytes);

        Reserve(bytes.LongLength);
        try
        {
            var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            BeforeStageFileCreateForTest?.Invoke(sessionRoot);
            string? stagedPath = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                await using var source = new MemoryStream(bytes, writable: false);
                var staged = await storage.CreateAsync(sessionId, source);
                stagedPath = staged.Path;
                if (staged.Size != bytes.LongLength)
                    throw new McpHubProviderPolicyException("The staged upload changed size while it was being captured.");
                AfterStageFileCreateForTest?.Invoke(stagedPath);
                storage.DeleteCreated(stagedPath);
                stagedPath = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                throw new McpHubProviderPolicyException("The staged upload could not be captured safely.");
            }
            finally
            {
                if (stagedPath is not null)
                {
                    try { storage.DeleteCreated(stagedPath); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
            }

            // The handle-anchored file is an auditable staging boundary, not the upload source.
            // Shortcut receives only the immutable caller bytes retained below, never a host path.

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var entry = new Entry(ownerKey, safeName, contentType, bytes, now.Add(Lifetime));
            if (!entries.TryAdd(handle, entry))
                throw new InvalidOperationException("Failed to allocate a unique upload handle.");

            return new StagedShortcutUpload(handle, safeName, contentType, bytes.LongLength, entry.ExpiresAtUtc);
        }
        catch
        {
            Release(bytes.LongLength);
            throw;
        }
    }

    public ShortcutUploadLease Open(string ownerKey, string handle)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        CleanupExpired();

        if (!IsOpaqueHandle(handle) || !entries.TryGetValue(handle, out var entry))
            throw new McpHubProviderPolicyException("The staged upload handle is invalid or expired.");
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(entry.OwnerKey)),
                SHA256.HashData(Encoding.UTF8.GetBytes(ownerKey))))
            throw new McpHubProviderPolicyException("The staged upload handle belongs to a different caller.");
        if (Interlocked.CompareExchange(ref entry.State, 1, 0) != 0)
            throw new McpHubProviderPolicyException("The staged upload handle is already consumed or in use.");

        try
        {
            BeforeUploadLeaseForTest?.Invoke(ContainedPath(handle));
            var stream = new MemoryStream(entry.Content, 0, entry.Content.Length, writable: false, publiclyVisible: false);
            return new ShortcutUploadLease(this, handle, entry, stream);
        }
        catch
        {
            Consume(handle, entry);
            throw;
        }
    }

    private void Consume(string handle, Entry entry)
    {
        if (entries.TryRemove(new KeyValuePair<string, Entry>(handle, entry)))
            Release(entry.Content.LongLength);
        Volatile.Write(ref entry.State, 2);
    }

    private void Reserve(long size)
    {
        while (true)
        {
            var current = Volatile.Read(ref reservedBytes);
            if (size > MaxStagedBytes - current)
                throw new McpHubProviderPolicyException("The upload staging area has reached its bounded capacity.");
            if (Interlocked.CompareExchange(ref reservedBytes, current + size, current) == current)
                return;
        }
    }

    private void Release(long size) => Interlocked.Add(ref reservedBytes, -size);

    private static string ValidateDisplayName(string displayName)
    {
        var value = displayName?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 180 ||
            value is "." or ".." ||
            value.IndexOfAny(['/', '\\', ':', '\0']) >= 0 ||
            value.Any(char.IsControl) ||
            Path.IsPathRooted(value))
        {
            throw new McpHubProviderPolicyException("displayName must be a plain filename without path components.");
        }

        return value;
    }

    private static void ValidateContent(string extension, byte[] bytes)
    {
        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".json": ValidateJson(bytes); break;
                case ".yaml":
                case ".yml": ValidateYaml(bytes); break;
                case ".csv": ValidateDelimited(bytes, ','); break;
                case ".tsv": ValidateDelimited(bytes, '\t'); break;
                case ".docx": ValidateOfficePackage(bytes, OfficeKind.Word); break;
                case ".xlsx": ValidateOfficePackage(bytes, OfficeKind.Spreadsheet); break;
                case ".pptx": ValidateOfficePackage(bytes, OfficeKind.Presentation); break;
                case ".pdf": ValidatePdf(bytes); break;
                case ".png": ValidatePng(bytes); break;
                case ".jpg":
                case ".jpeg": ValidateJpeg(bytes); break;
                case ".gif": ValidateGif(bytes); break;
                case ".webp": ValidateWebp(bytes); break;
                default: _ = DecodeSafeText(bytes); break;
            }
        }
        catch (McpHubProviderPolicyException ex)
        {
            throw new McpHubProviderPolicyException(
                $"The staged content does not match the allowed '{extension}' file type: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidDataException or XmlException or YamlException or IOException or OverflowException)
        {
            throw new McpHubProviderPolicyException($"The staged content does not match the allowed '{extension}' file type.");
        }
    }

    private static void ValidateJson(byte[] bytes)
    {
        _ = DecodeSafeText(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
    }

    private static void ValidateYaml(byte[] bytes)
    {
        var parser = new Parser(new StringReader(DecodeSafeText(bytes)));
        var eventCount = 0;
        while (parser.MoveNext())
        {
            if (++eventCount > 100_000)
                throw new McpHubProviderPolicyException("The YAML attachment is too structurally complex.");
        }
    }

    private static void ValidateDelimited(byte[] bytes, char delimiter)
    {
        var text = DecodeSafeText(bytes);
        var quoted = false;
        var closedQuote = false;
        var fieldStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                    continue;
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                quoted = false;
                closedQuote = true;
                continue;
            }

            if (closedQuote)
            {
                if (c != delimiter && c is not '\r' and not '\n')
                    throw new McpHubProviderPolicyException("Delimited attachments may not contain characters after a closing quote.");
                closedQuote = false;
                fieldStart = true;
                continue;
            }

            if (c == '"')
            {
                if (!fieldStart)
                    throw new McpHubProviderPolicyException("Delimited fields may only open quotes at a field boundary.");
                quoted = true;
                fieldStart = false;
                continue;
            }

            fieldStart = c == delimiter || c is '\r' or '\n';
        }

        if (quoted)
            throw new McpHubProviderPolicyException("Delimited attachment contains an unterminated quoted field.");
    }

    private static void ValidateOfficePackage(byte[] bytes, OfficeKind kind)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaxOfficeEntries)
            throw new McpHubProviderPolicyException("The Office attachment contains an invalid number of package entries.");

        long expandedBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') ||
                name.Split('/').Any(segment => segment is ".." or ".") || !names.Add(name))
                throw new McpHubProviderPolicyException("The Office attachment contains an unsafe or duplicate package path.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedOfficeBytes)
                throw new McpHubProviderPolicyException("The Office attachment expands beyond the allowed package size.");
        }

        var contentTypes = archive.GetEntry("[Content_Types].xml");
        var rootRelationships = archive.GetEntry("_rels/.rels");
        if (contentTypes is null || rootRelationships is null ||
            contentTypes.Length > MaxContentTypesBytes || rootRelationships.Length > MaxContentTypesBytes)
            throw new McpHubProviderPolicyException("The Office attachment is missing its required package parts.");

        using var contentTypesStream = contentTypes.Open();
        using var reader = XmlReader.Create(contentTypesStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxContentTypesBytes,
            XmlResolver = null,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        if (document.Root?.Name != ns + "Types")
            throw new McpHubProviderPolicyException("The Office attachment has an invalid content-types root element.");

        using var packageStream = new MemoryStream(bytes, writable: false);
        var openSettings = new OpenSettings
        {
            AutoSave = false,
            MaxCharactersInPart = MaxExpandedOfficeBytes,
        };
        using OpenXmlPackage package = kind switch
        {
            OfficeKind.Word => WordprocessingDocument.Open(packageStream, false, openSettings),
            OfficeKind.Spreadsheet => SpreadsheetDocument.Open(packageStream, false, openSettings),
            OfficeKind.Presentation => PresentationDocument.Open(packageStream, false, openSettings),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var hasExpectedRoot = package switch
        {
            WordprocessingDocument word => word.MainDocumentPart?.Document is not null,
            SpreadsheetDocument sheet => sheet.WorkbookPart?.Workbook is not null,
            PresentationDocument presentation => presentation.PresentationPart?.Presentation is not null,
            _ => false,
        };
        if (!hasExpectedRoot)
            throw new McpHubProviderPolicyException("The Office attachment is missing the expected typed primary document root.");

        var validationError = new OpenXmlValidator().Validate(package).FirstOrDefault();
        if (validationError is not null)
            throw new McpHubProviderPolicyException($"The Office attachment fails package/schema validation: {validationError.Description}");
    }

    private static void ValidatePdf(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new McpHubProviderPolicyException("The PDF attachment has an invalid signature.");
        try
        {
            using var document = PdfReader.Open(new MemoryStream(bytes, writable: false), PdfDocumentOpenMode.Import);
            _ = document.PageCount;
            _ = document.Internals.Catalog;
        }
        catch (Exception ex) when (ex is not McpHubProviderPolicyException)
        {
            throw new McpHubProviderPolicyException($"The PDF attachment is not a structurally valid document: {ex.Message}");
        }
    }

    private static void ValidatePng(byte[] bytes)
    {
        ValidateImage(bytes, "PNG");
    }

    private static void ValidateJpeg(byte[] bytes)
    {
        ValidateImage(bytes, "JPEG");
    }

    private static void ValidateGif(byte[] bytes)
    {
        ValidateImage(bytes, "GIF");
    }

    private static void ValidateWebp(byte[] bytes)
    {
        ValidateImage(bytes, "WEBP");
    }

    private static void ValidateImage(byte[] bytes, string expectedFormat)
    {
        try
        {
            var info = Image.Identify(ImageIdentificationOptions, bytes);
            if (!string.Equals(info.Metadata.DecodedImageFormat?.Name, expectedFormat, StringComparison.OrdinalIgnoreCase))
                throw new McpHubProviderPolicyException($"The image encoding is not {expectedFormat}.");
            if (info.Width <= 0 || info.Height <= 0)
                throw new McpHubProviderPolicyException("The image dimensions are invalid.");

            // Some single-frame decoders omit the redundant per-frame metadata entry.
            var frameCount = Math.Max(1, info.FrameMetadataCollection.Count);
            if (frameCount > MaxImageFrames)
                throw new McpHubProviderPolicyException(
                    "The image frame count exceeds the safe limit before pixel decoding.");

            var aggregateFrameArea = checked((long)info.Width * info.Height * frameCount);
            if (aggregateFrameArea > MaxAggregateImagePixels)
                throw new McpHubProviderPolicyException(
                    "The image aggregate frame area exceeds the safe limit before pixel decoding.");

            // The upload gateway only needs to establish the declared file type and enforce resource
            // bounds. Fully decoding untrusted pixels here would create a decompression-bomb primitive
            // in a privileged process without making the opaque Shortcut attachment safer.
        }
        catch (McpHubProviderPolicyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpHubProviderPolicyException($"The image metadata cannot be safely identified: {ex.Message}");
        }
    }

    private static string DecodeSafeText(byte[] bytes)
    {
        if (bytes.Contains((byte)0))
            throw new McpHubProviderPolicyException("Text attachments may not contain NUL bytes.");
        return StrictUtf8.GetString(bytes);
    }

    private string ContainedPath(string handle)
    {
        var candidate = Path.GetFullPath(Path.Combine(sessionRoot, handle));
        var prefix = sessionRoot.EndsWith(Path.DirectorySeparatorChar) ? sessionRoot : sessionRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
            throw new McpHubProviderPolicyException("The staged upload path escaped its isolated root.");
        return candidate;
    }

    private void CleanupExpired()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var pair in entries)
        {
            if (pair.Value.ExpiresAtUtc > now || Volatile.Read(ref pair.Value.State) != 0)
                continue;
            if (entries.TryRemove(pair))
                Release(pair.Value.Content.LongLength);
        }
    }

    private void MaintainSafely()
    {
        try
        {
            CleanupExpired();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static string CreatePrivateStagingParent(string? rootPath)
    {
        var parent = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Path.GetTempPath(), "codegraph-shortcut-upload-staging")
            : rootPath);
        if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new McpHubProviderPolicyException("The configured upload staging root may not be a symlink or reparse point.");
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(parent);
        else
            Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new McpHubProviderPolicyException("The upload staging root is not a real directory.");
        return Path.TrimEndingDirectorySeparator(parent);
    }

    private static bool IsOpaqueHandle(string? handle) =>
        handle is { Length: 64 } && handle.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static long CreateSessionId()
    {
        long value;
        do
        {
            value = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue;
        } while (value == 0);
        return value;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        cleanupTimer.Dispose();
        entries.Clear();
        Interlocked.Exchange(ref reservedBytes, 0);
        try { storage.DeleteContainerIfEmpty(sessionId); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (Win32Exception) { }
        finally { storage.Dispose(); }
    }

    internal sealed class Entry(
        string ownerKey,
        string displayName,
        string contentType,
        byte[] content,
        DateTime expiresAtUtc)
    {
        public string OwnerKey { get; } = ownerKey;
        public string DisplayName { get; } = displayName;
        public string ContentType { get; } = contentType;
        public byte[] Content { get; } = content;
        public long SizeBytes => Content.LongLength;
        public DateTime ExpiresAtUtc { get; } = expiresAtUtc;
        public int State;
    }

    public sealed class ShortcutUploadLease : IAsyncDisposable, IDisposable
    {
        private readonly ShortcutUploadStagingArea owner;
        private readonly string handle;
        private readonly Entry entry;
        private int finalized;

        internal ShortcutUploadLease(ShortcutUploadStagingArea owner, string handle, Entry entry, Stream stream)
        {
            this.owner = owner;
            this.handle = handle;
            this.entry = entry;
            Stream = stream;
        }

        public Stream Stream { get; }
        public string DisplayName => entry.DisplayName;
        public string ContentType => entry.ContentType;
        public long SizeBytes => entry.SizeBytes;

        public void Complete() => Finish();

        public void Dispose()
        {
            Stream.Dispose();
            Finish();
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            Finish();
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref finalized, 1) == 0)
                owner.Consume(handle, entry);
        }
    }

    private enum OfficeKind { Word, Spreadsheet, Presentation }
}

public sealed record StagedShortcutUpload(
    string Handle,
    string DisplayName,
    string ContentType,
    long SizeBytes,
    DateTime ExpiresAtUtc);
