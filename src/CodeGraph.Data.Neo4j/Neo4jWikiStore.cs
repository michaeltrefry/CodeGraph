using Neo4j.Driver;

namespace CodeGraph.Data.Neo4j;

public class Neo4jWikiStore(Neo4jSessionFactory sessionFactory) : IWikiStore
{
    // ── Sections ──

    public async Task<IReadOnlyList<WikiSectionEntity>> ListSectionsAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:WikiSection) RETURN s ORDER BY s.sortOrder");
            var results = new List<WikiSectionEntity>();
            await foreach (var record in cursor)
                results.Add(MapSection(record["s"].As<INode>()));
            return results;
        });
    }

    public async Task<WikiSectionEntity?> GetSectionBySlugAsync(string slug)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:WikiSection {slug: $slug}) RETURN s",
                new { slug });
            return await cursor.FetchAsync() ? MapSection(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<WikiSectionEntity?> GetSectionByIdAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (s:WikiSection {appId: $id}) RETURN s",
                new { id });
            return await cursor.FetchAsync() ? MapSection(cursor.Current["s"].As<INode>()) : null;
        });
    }

    public async Task<int> CountSectionsAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("MATCH (s:WikiSection) RETURN count(s) AS cnt");
            await cursor.FetchAsync();
            return cursor.Current["cnt"].As<int>();
        });
    }

    public async Task<WikiSectionEntity> CreateSectionAsync(WikiSectionEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "WikiSection");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (s:WikiSection {
                    appId: $id, slug: $slug, title: $title,
                    description: $description, icon: $icon,
                    sortOrder: $sortOrder, isSystem: $isSystem,
                    allowUserPages: $allowUserPages, hasRawContent: $hasRawContent,
                    createdAt: $createdAt, updatedAt: $updatedAt
                })
                """,
                SectionParams(entity));
        });
        return entity;
    }

    public async Task UpdateSectionAsync(WikiSectionEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (s:WikiSection {appId: $id})
                SET s.title = $title, s.description = $description, s.icon = $icon,
                    s.sortOrder = $sortOrder, s.allowUserPages = $allowUserPages,
                    s.hasRawContent = $hasRawContent, s.updatedAt = $updatedAt
                """,
                SectionParams(entity));
        });
    }

    public async Task DeleteSectionAsync(WikiSectionEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete revisions of pages in this section
            await tx.RunAsync("""
                MATCH (s:WikiSection {appId: $id})-[:HAS_PAGE]->(p:WikiPage)-[:HAS_REVISION]->(r:WikiRevision)
                DETACH DELETE r
                """, new { id = entity.Id });
            // Delete attachments of pages in this section
            await tx.RunAsync("""
                MATCH (s:WikiSection {appId: $id})-[:HAS_PAGE]->(p:WikiPage)-[:HAS_ATTACHMENT]->(a:WikiAttachment)
                DETACH DELETE a
                """, new { id = entity.Id });
            // Delete pages
            await tx.RunAsync("""
                MATCH (s:WikiSection {appId: $id})-[:HAS_PAGE]->(p:WikiPage)
                DETACH DELETE p
                """, new { id = entity.Id });
            // Delete section
            await tx.RunAsync(
                "MATCH (s:WikiSection {appId: $id}) DETACH DELETE s",
                new { id = entity.Id });
        });
    }

    // ── Pages ──

    public async Task<WikiPageEntity?> GetPageByIdAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (p:WikiPage {appId: $id}) RETURN p",
                new { id });
            return await cursor.FetchAsync() ? MapPage(cursor.Current["p"].As<INode>()) : null;
        });
    }

    public async Task<WikiPageEntity?> FindPageAsync(long sectionId, long? parentId, string slug)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (p:WikiPage {sectionId: $sectionId, slug: $slug})
                WHERE p.parentId = $parentId
                RETURN p
                """,
                new { sectionId, parentId = parentId ?? -1L, slug });
            return await cursor.FetchAsync() ? MapPage(cursor.Current["p"].As<INode>()) : null;
        });
    }

    public async Task<IReadOnlyList<WikiPageEntity>> GetPagesBySectionAsync(long sectionId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (p:WikiPage {sectionId: $sectionId})
                RETURN p ORDER BY p.sortOrder, p.title
                """,
                new { sectionId });
            var results = new List<WikiPageEntity>();
            await foreach (var record in cursor)
                results.Add(MapPage(record["p"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<WikiPageEntity>> GetAutoGeneratedPagesBySectionAsync(long sectionId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (p:WikiPage {sectionId: $sectionId, isAutoGenerated: true})
                RETURN p
                """,
                new { sectionId });
            var results = new List<WikiPageEntity>();
            await foreach (var record in cursor)
                results.Add(MapPage(record["p"].As<INode>()));
            return results;
        });
    }

    public async Task<IReadOnlyList<WikiPageEntity>> SearchPagesAsync(long sectionId, string pattern)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var lowerPattern = pattern.ToLowerInvariant();
            var cursor = await tx.RunAsync("""
                MATCH (p:WikiPage {sectionId: $sectionId})
                WHERE p.slug CONTAINS $pattern
                   OR toLower(p.title) CONTAINS $pattern
                RETURN p
                """,
                new { sectionId, pattern = lowerPattern });
            var results = new List<WikiPageEntity>();
            await foreach (var record in cursor)
                results.Add(MapPage(record["p"].As<INode>()));
            return results;
        });
    }

    public async Task<int> GetMaxSortOrderAsync(long sectionId, long? parentId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (p:WikiPage {sectionId: $sectionId})
                WHERE p.parentId = $parentId
                RETURN max(p.sortOrder) AS maxSort
                """,
                new { sectionId, parentId = parentId ?? -1L });
            await cursor.FetchAsync();
            var val = cursor.Current["maxSort"];
            return val is null or DBNull ? -1 : val.As<int>();
        });
    }

    public async Task<WikiPageEntity> CreatePageAsync(WikiPageEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "WikiPage");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (p:WikiPage {
                    appId: $id, sectionId: $sectionId, parentId: $parentId,
                    slug: $slug, title: $title, content: $content, rawContent: $rawContent,
                    author: $author, revision: $revision, sortOrder: $sortOrder,
                    isAutoGenerated: $isAutoGenerated, depth: $depth,
                    createdAt: $createdAt, updatedAt: $updatedAt
                })
                """,
                PageParams(entity));
        });
        return entity;
    }

    public async Task UpdatePageAsync(WikiPageEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                MATCH (p:WikiPage {appId: $id})
                SET p.sectionId = $sectionId, p.parentId = $parentId,
                    p.title = $title, p.content = $content, p.rawContent = $rawContent,
                    p.author = $author, p.revision = $revision, p.sortOrder = $sortOrder,
                    p.depth = $depth, p.updatedAt = $updatedAt
                """,
                PageParams(entity));
        });
    }

    public async Task DeletePageAsync(WikiPageEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            // Delete all children (and their revisions/attachments) in one query
            await tx.RunAsync("""
                MATCH (p:WikiPage {appId: $id})
                OPTIONAL MATCH (p)<-[:CHILD_OF*]-(child:WikiPage)
                OPTIONAL MATCH (child)-[:HAS_REVISION]->(cr:WikiRevision)
                OPTIONAL MATCH (child)-[:HAS_ATTACHMENT]->(ca:WikiAttachment)
                DETACH DELETE cr, ca, child
                """, new { id = entity.Id });

            // Delete self and own revisions/attachments
            await tx.RunAsync("""
                MATCH (p:WikiPage {appId: $id})
                OPTIONAL MATCH (p)-[:HAS_REVISION]->(r:WikiRevision)
                OPTIONAL MATCH (p)-[:HAS_ATTACHMENT]->(a:WikiAttachment)
                DETACH DELETE r, a, p
                """, new { id = entity.Id });
        });
    }

    // ── Revisions ──

    public async Task<IReadOnlyList<WikiRevisionEntity>> GetRevisionsAsync(long pageId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (r:WikiRevision {pageId: $pageId})
                RETURN r ORDER BY r.revision DESC
                """,
                new { pageId });
            var results = new List<WikiRevisionEntity>();
            await foreach (var record in cursor)
                results.Add(MapRevision(record["r"].As<INode>()));
            return results;
        });
    }

    public async Task<WikiRevisionEntity?> GetRevisionAsync(long pageId, int revision)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (r:WikiRevision {pageId: $pageId, revision: $revision})
                RETURN r
                """,
                new { pageId, revision });
            return await cursor.FetchAsync() ? MapRevision(cursor.Current["r"].As<INode>()) : null;
        });
    }

    public async Task CreateRevisionAsync(WikiRevisionEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "WikiRevision");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (r:WikiRevision {
                    appId: $id, pageId: $pageId, revision: $revision,
                    title: $title, content: $content, rawContent: $rawContent,
                    author: $author, createdAt: $createdAt
                })
                """,
                new
                {
                    id = entity.Id,
                    pageId = entity.PageId,
                    revision = entity.Revision,
                    title = entity.Title,
                    content = entity.Content,
                    rawContent = entity.RawContent,
                    author = entity.Author,
                    createdAt = entity.CreatedAt
                });
        });
    }

    // ── Attachments ──

    public async Task<IReadOnlyList<WikiAttachmentEntity>> ListAttachmentsAsync(long pageId)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (a:WikiAttachment {pageId: $pageId})
                RETURN a ORDER BY a.filename
                """,
                new { pageId });
            var results = new List<WikiAttachmentEntity>();
            await foreach (var record in cursor)
                results.Add(MapAttachment(record["a"].As<INode>()));
            return results;
        });
    }

    public async Task<WikiAttachmentEntity?> GetAttachmentByIdAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (a:WikiAttachment {appId: $id}) RETURN a",
                new { id });
            return await cursor.FetchAsync() ? MapAttachment(cursor.Current["a"].As<INode>()) : null;
        });
    }

    public async Task<WikiAttachmentEntity> CreateAttachmentAsync(WikiAttachmentEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        entity.Id = await NextId(session, "WikiAttachment");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("""
                CREATE (a:WikiAttachment {
                    appId: $id, pageId: $pageId, filename: $filename,
                    storagePath: $storagePath, contentType: $contentType,
                    sizeBytes: $sizeBytes, uploadedBy: $uploadedBy,
                    createdAt: $createdAt
                })
                """,
                new
                {
                    id = entity.Id,
                    pageId = entity.PageId,
                    filename = entity.Filename,
                    storagePath = entity.StoragePath,
                    contentType = entity.ContentType,
                    sizeBytes = entity.SizeBytes,
                    uploadedBy = entity.UploadedBy,
                    createdAt = entity.CreatedAt
                });
        });
        return entity;
    }

    public async Task DeleteAttachmentAsync(WikiAttachmentEntity entity)
    {
        await using var session = sessionFactory.GetSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (a:WikiAttachment {appId: $id}) DETACH DELETE a",
                new { id = entity.Id });
        });
    }

    // ── ID Generation ──

    private static async Task<long> NextId(IAsyncSession session, string label)
    {
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (c:IdCounter {label: $label})
                ON CREATE SET c.current = 1
                ON MATCH SET c.current = c.current + 1
                RETURN c.current AS id
                """,
                new { label });
            await cursor.FetchAsync();
            return cursor.Current["id"].As<long>();
        });
    }

    // ── Mapping Helpers ──

    private static object SectionParams(WikiSectionEntity e) => new
    {
        id = e.Id,
        slug = e.Slug,
        title = e.Title,
        description = e.Description,
        icon = e.Icon,
        sortOrder = e.SortOrder,
        isSystem = e.IsSystem,
        allowUserPages = e.AllowUserPages,
        hasRawContent = e.HasRawContent,
        createdAt = e.CreatedAt,
        updatedAt = e.UpdatedAt
    };

    private static object PageParams(WikiPageEntity e) => new
    {
        id = e.Id,
        sectionId = e.SectionId,
        parentId = e.ParentId ?? -1L,
        slug = e.Slug,
        title = e.Title,
        content = e.Content,
        rawContent = e.RawContent,
        author = e.Author,
        revision = e.Revision,
        sortOrder = e.SortOrder,
        isAutoGenerated = e.IsAutoGenerated,
        depth = e.Depth,
        createdAt = e.CreatedAt,
        updatedAt = e.UpdatedAt
    };

    private static WikiSectionEntity MapSection(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        Slug = node["slug"].As<string>(),
        Title = node["title"].As<string>(),
        Description = GetStr(node, "description"),
        Icon = GetStr(node, "icon"),
        SortOrder = node["sortOrder"].As<int>(),
        IsSystem = node.Properties.ContainsKey("isSystem") && node["isSystem"].As<bool>(),
        AllowUserPages = !node.Properties.ContainsKey("allowUserPages") || node["allowUserPages"].As<bool>(),
        HasRawContent = node.Properties.ContainsKey("hasRawContent") && node["hasRawContent"].As<bool>(),
        CreatedAt = GetDateTime(node, "createdAt"),
        UpdatedAt = GetDateTime(node, "updatedAt")
    };

    private static WikiPageEntity MapPage(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        SectionId = node["sectionId"].As<long>(),
        ParentId = node["parentId"].As<long>() == -1L ? null : node["parentId"].As<long>(),
        Slug = node["slug"].As<string>(),
        Title = node["title"].As<string>(),
        Content = node["content"].As<string>(),
        RawContent = GetStr(node, "rawContent"),
        Author = node["author"].As<string>(),
        Revision = node["revision"].As<int>(),
        SortOrder = node["sortOrder"].As<int>(),
        IsAutoGenerated = node.Properties.ContainsKey("isAutoGenerated") && node["isAutoGenerated"].As<bool>(),
        Depth = node["depth"].As<int>(),
        CreatedAt = GetDateTime(node, "createdAt"),
        UpdatedAt = GetDateTime(node, "updatedAt")
    };

    private static WikiRevisionEntity MapRevision(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        PageId = node["pageId"].As<long>(),
        Revision = node["revision"].As<int>(),
        Title = node["title"].As<string>(),
        Content = node["content"].As<string>(),
        RawContent = GetStr(node, "rawContent"),
        Author = node["author"].As<string>(),
        CreatedAt = GetDateTime(node, "createdAt")
    };

    private static WikiAttachmentEntity MapAttachment(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        PageId = node["pageId"].As<long>(),
        Filename = node["filename"].As<string>(),
        StoragePath = node["storagePath"].As<string>(),
        ContentType = node["contentType"].As<string>(),
        SizeBytes = node["sizeBytes"].As<long>(),
        UploadedBy = node["uploadedBy"].As<string>(),
        CreatedAt = GetDateTime(node, "createdAt")
    };

    private static string? GetStr(INode node, string key)
        => node.Properties.TryGetValue(key, out var val) && val is not null
            ? val.As<string>()
            : null;

    private static DateTime GetDateTime(INode node, string key)
    {
        if (!node.Properties.TryGetValue(key, out var val) || val is null)
            return DateTime.MinValue;
        if (val is LocalDateTime ldt) return ldt.ToDateTime();
        if (val is ZonedDateTime zdt) return zdt.ToDateTimeOffset().UtcDateTime;
        if (val is string s && DateTime.TryParse(s, out var dt)) return dt;
        return DateTime.MinValue;
    }
}
