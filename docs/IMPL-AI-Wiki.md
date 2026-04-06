# Implementation Plan: AI Wiki

**Status:** In Progress — Phases 1-4 Complete, Phase 5 Remaining
**Date:** 2026-03-21
**PRD:** [PRD-AI-Wiki.md](PRD-AI-Wiki.md)
**Branch:** Wiki-Features

---

## Phase 1: Auth & Admin Foundation

**Goal:** Authenticated users can sign in, admins are identified, and admin pages exist for settings/operations/user management.

### 1.1 — Database: Admin & Settings Tables

**Migration:** `sql/migrations/005_admin_foundation.sql`

Create tables:
- `admin_users` (id, username, created_at)
- `settings_overrides` (id, settings_json, updated_by, updated_at)

Seed initial admin:
```sql
INSERT INTO admin_users (username, created_at) VALUES ('mtrefry', NOW());
```

**Files to create/modify:**
- `sql/migrations/005_admin_foundation.sql` — new migration script

### 1.2 — EF Core Entities & DbContext

Add entity classes and DbSet registrations.

**Files to modify:**
- `src/CodeGraph.Data/Entities.cs` — add `AdminUserEntity`, `SettingsOverrideEntity`
- `src/CodeGraph.Data/CodeGraphDbContext.cs` — add `DbSet<AdminUserEntity>`, `DbSet<SettingsOverrideEntity>`, fluent config

### 1.3 — Configuration Classes

Add `AuthOptions` and `WikiOptions` to settings.

**Files to create/modify:**
- `src/CodeGraph.Services/Configuration/CodeGraphServiceSettings.cs` — add `WikiOptions` and `AuthOptions` properties
- `src/CodeGraph.Services/Configuration/WikiOptions.cs` — new file
- `src/CodeGraph.Services/Configuration/AuthOptions.cs` — new file

### 1.4 — API JWT Bearer Authentication

Wire up ASP.NET JWT bearer middleware. Use `AuthOptions.Authority` for OIDC discovery.

**Files to modify:**
- `src/CodeGraph/Startup.cs` — add `AddAuthentication().AddJwtBearer()`, configure from `AuthOptions`

### 1.5 — Admin Role Middleware

Create an authorization policy that checks `admin_users` table. Use ASP.NET `IAuthorizationHandler` with a custom `AdminRequirement`.

**Files to create:**
- `src/CodeGraph/Auth/AdminRequirement.cs` — `IAuthorizationRequirement`
- `src/CodeGraph/Auth/AdminAuthorizationHandler.cs` — checks username against `admin_users` table

**Files to modify:**
- `src/CodeGraph/Startup.cs` — register `AdminAuthorizationHandler`, add `"Admin"` policy

### 1.6 — Settings Override Service

Service to load settings from DB and merge over Consul config. Single-row upsert pattern.

**Files to create:**
- `src/CodeGraph.Services/ISettingsService.cs` — interface: `GetEffectiveSettingsAsync()`, `UpdateOverridesAsync(json, username)`
- `src/CodeGraph.Services/SettingsService.cs` — loads `TcConfiguration<CodeGraphServiceSettings>`, merges `settings_overrides` JSON on top, excludes ConnectionString on GET

**Files to modify:**
- `src/CodeGraph/Startup.cs` — register `ISettingsService`

### 1.7 — Admin Users Service

CRUD for the `admin_users` table. Also used by the authorization handler in 1.5.

**Files to create:**
- `src/CodeGraph.Services/IAdminUserService.cs` — `ListAsync()`, `AddAsync(username)`, `RemoveAsync(username)`, `IsAdminAsync(username)`
- `src/CodeGraph.Services/AdminUserService.cs`

**Files to modify:**
- `src/CodeGraph/Startup.cs` — register `IAdminUserService`

### 1.8 — Admin API Endpoints (Settings, Users, MCP Regenerate)

Extend `AdminController` with new endpoints. All require `[Authorize(Policy = "Admin")]`.

**Files to modify:**
- `src/CodeGraph/Controllers/AdminController.cs` — add:
  - `GET /api/admin/settings` → returns effective settings JSON (no ConnectionString)
  - `PUT /api/admin/settings` → updates `settings_overrides`
  - `GET /api/admin/admins` → list admin usernames
  - `POST /api/admin/admins` → add admin
  - `DELETE /api/admin/admins/{username}` → remove admin

**Note:** Existing admin endpoints (`processRepos`, `reIndexAll`, `link`, `discover`, `processBatchAnalysis`) should also get `[Authorize(Policy = "Admin")]`.

### 1.9 — Angular: Auth Service & Login Flow

Install `oidc-client-ts` (lightweight, no framework dependency). Create auth service with PKCE flow.

**Files to create:**
- `CodeGraphWeb/src/app/core/auth.service.ts` — wraps `oidc-client-ts` UserManager; methods: `login()`, `handleCallback()`, `logout()`, `getUser()`, `getToken()`, `isAuthenticated()`, `isAdmin()`
- `CodeGraphWeb/src/app/core/auth.interceptor.ts` — HTTP interceptor to attach Bearer token
- `CodeGraphWeb/src/app/core/admin.guard.ts` — route guard for admin pages
- `CodeGraphWeb/src/app/pages/auth-callback/auth-callback.component.ts` — handles OAuth redirect

**Files to modify:**
- `CodeGraphWeb/package.json` — add `oidc-client-ts`
- `CodeGraphWeb/src/app/app.routes.ts` — add `/auth/callback` route
- `CodeGraphWeb/src/app/app.config.ts` — register HTTP interceptor
- `CodeGraphWeb/src/app/app.component.ts` — add Sign In / username display in header

### 1.10 — Angular: Admin Pages

Build admin UI pages. All guarded by `AdminGuard`.

**Files to create:**
- `CodeGraphWeb/src/app/pages/admin/admin-layout.component.ts` — shell with admin nav tabs
- `CodeGraphWeb/src/app/pages/admin/admin-settings.component.ts` — JSON editor for settings (use `textarea` with JSON validation initially; upgrade to monaco later if desired)
- `CodeGraphWeb/src/app/pages/admin/admin-users.component.ts` — list/add/remove admin usernames
- `CodeGraphWeb/src/app/pages/admin/admin-operations.component.ts` — buttons for existing admin endpoints + future MCP regenerate

**Files to modify:**
- `CodeGraphWeb/src/app/app.routes.ts` — add `/admin/**` routes with guard
- `CodeGraphWeb/src/app/core/api.service.ts` — add admin API methods

### Phase 1 Verification

- [ ] Can sign in via OAuth2, token is cached and sent on API calls
- [ ] Admin users table seeded; `IsAdmin` check works
- [ ] `GET/PUT /api/admin/settings` returns/saves settings (minus ConnectionString)
- [ ] `GET/POST/DELETE /api/admin/admins` manages admin list
- [ ] Existing admin endpoints require admin auth
- [ ] Angular admin pages accessible only to admins
- [ ] Anonymous users can still browse existing read-only pages

---

## Phase 2: Wiki Data Model & API

**Goal:** All wiki tables exist, conventions are migrated, full CRUD API works, MCP tools repointed.

### 2.1 — Database: Wiki Tables & Data Migration

**Migration:** `sql/migrations/006_wiki.sql`

Create tables:
- `wiki_sections` (with seed: General, Conventions, Skills, Agents, MCP Documentation)
- `wiki_pages`
- `wiki_revisions`
- `wiki_attachments`

Migrate data:
- Copy `convention_pages` → `wiki_pages` (section_id = Conventions)
- Copy `convention_revisions` → `wiki_revisions` (map page_ids)
- Drop `convention_pages`, `convention_revisions`

**Files to create:**
- `sql/migrations/006_wiki.sql`

### 2.2 — EF Core Entities & DbContext

**Files to modify:**
- `src/CodeGraph.Data/Entities.cs` — add `WikiSectionEntity`, `WikiPageEntity`, `WikiRevisionEntity`, `WikiAttachmentEntity`
- `src/CodeGraph.Data/CodeGraphDbContext.cs` — add DbSets, remove ConventionPages/ConventionRevisions DbSets, add fluent config (unique constraints, cascades, indexes)

### 2.3 — Request/Response Models

**Files to create:**
- `src/CodeGraph.Models/Requests/WikiPageRequest.cs` — title, content, parentId?, slug?, sortOrder?
- `src/CodeGraph.Models/Requests/WikiSectionRequest.cs` — title, description?, icon?, sortOrder?, allowUserPages?
- `src/CodeGraph.Models/Requests/WikiPageMoveRequest.cs` — newParentId?, newSectionId?, sortOrder?
- `src/CodeGraph.Models/Responses/WikiResponses.cs` — `WikiSectionResponse`, `WikiPageResponse`, `WikiPageListItem`, `WikiTreeNode`, `WikiRevisionListItem`, `WikiRevisionResponse`, `WikiAttachmentResponse`

### 2.4 — Wiki Service

Core business logic for sections, pages, revisions, and tree navigation.

**Files to create:**
- `src/CodeGraph.Services/IWikiService.cs` — interface covering:
  - Sections: `ListSectionsAsync()`, `GetSectionTreeAsync(sectionSlug)`
  - Pages: `GetPageAsync(sectionSlug, path)`, `CreatePageAsync(sectionSlug, parentPath?, request)`, `UpdatePageAsync(sectionSlug, path, request)`, `DeletePageAsync(sectionSlug, path)`, `MovePageAsync(sectionSlug, path, moveRequest)`
  - Revisions: `GetRevisionsAsync(sectionSlug, path)`, `GetRevisionAsync(sectionSlug, path, revision)`
  - Admin: `CreateSectionAsync(request)`, `UpdateSectionAsync(id, request)`, `DeleteSectionAsync(id)`
- `src/CodeGraph.Services/WikiService.cs` — implementation
  - Path resolution: split `path` string into slug segments, walk `wiki_pages` from root
  - Depth enforcement: reject creates that would exceed level 4
  - Revision tracking: insert into `wiki_revisions` on every update
  - Slug generation: auto-generate from title, ensure unique within sibling scope
  - Section rules: respect `allow_user_pages` flag, block delete on `is_system` sections

**Files to modify:**
- `src/CodeGraph/Startup.cs` — register `IWikiService`

### 2.5 — Attachment Service

**Files to create:**
- `src/CodeGraph.Services/IAttachmentService.cs` — `ListAsync(pageId)`, `UploadAsync(pageId, file, username)`, `GetAsync(attachmentId)`, `DeleteAsync(attachmentId)`, `DeleteAllForPageAsync(pageId)` (best-effort file cleanup)
- `src/CodeGraph.Services/AttachmentService.cs` — filesystem storage under `WikiOptions.AttachmentStoragePath/{pageId}/{filename}`

**Files to modify:**
- `src/CodeGraph/Startup.cs` — register `IAttachmentService`

### 2.6 — Wiki Controller

**Files to create:**
- `src/CodeGraph/Controllers/WikiController.cs` — all routes from PRD Section 6:
  - Read routes: no auth
  - Write routes: `[Authorize]`
  - Extracts `username` from JWT claims for author field
  - Catch-all path parameter via `{**path}` for nested slugs

### 2.7 — Admin Section Endpoints

**Files to modify:**
- `src/CodeGraph/Controllers/AdminController.cs` — add section CRUD endpoints:
  - `GET /api/admin/sections`
  - `POST /api/admin/sections`
  - `PUT /api/admin/sections/{id}`
  - `DELETE /api/admin/sections/{id}`

### 2.8 — Repoint MCP Tools

Update `list_conventions` and `get_convention` to query `wiki_pages` where section = "conventions" instead of the old `convention_pages` table.

**Files to modify:**
- `src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs` — update both tool methods to use `IWikiService` or query `wiki_pages` directly

### 2.9 — Remove Convention System

Delete old convention code. Clean break as specified in PRD.

**Files to delete:**
- `src/CodeGraph/Controllers/ConventionsController.cs`
- `src/CodeGraph.Services/ConventionService.cs`
- `src/CodeGraph.Services/IConventionService.cs`
- `src/CodeGraph.Models/Messages/ConventionUpdated.cs`
- `src/CodeGraph/Consumers/ConventionUpdatedConsumer.cs`
- `src/CodeGraph.Models/Requests/ConventionRequest.cs`
- `src/CodeGraph.Models/Responses/ConventionResponse.cs` (or whatever files hold `ConventionListItem`, `ConventionDetailResponse`, etc.)

**Files to modify:**
- `src/CodeGraph.Data/Entities.cs` — remove `ConventionPageEntity`, `ConventionRevisionEntity`
- `src/CodeGraph.Data/CodeGraphDbContext.cs` — remove convention DbSets
- `src/CodeGraph/Startup.cs` — remove `IConventionService` registration, remove `ConventionUpdatedConsumer` MassTransit registration

### Phase 2 Verification

- [ ] `wiki_sections` seeded with 5 sections
- [ ] Convention data migrated to `wiki_pages` (verify page count matches)
- [ ] Full CRUD works: create/read/update/delete pages at various nesting levels
- [ ] Path-based routing resolves correctly (e.g., `GET /api/wiki/skills/commit-skill/skill-md`)
- [ ] Revision history preserved for migrated conventions
- [ ] Depth limit enforced (reject level 5 creates)
- [ ] Attachment upload/download/delete works, files on disk
- [ ] Page delete cascades to children and attachments (files cleaned up best-effort)
- [ ] `list_conventions` / `get_convention` MCP tools still work against new tables
- [ ] Old convention endpoints return 404
- [ ] `allow_user_pages = false` blocks page creation in MCP Docs section
- [ ] `is_system = true` blocks section deletion

---

## Phase 3: Angular Wiki UI

**Goal:** Full wiki browsing and editing experience with sidebar navigation tree.

### 3.1 — Angular API Methods

**Files to modify:**
- `CodeGraphWeb/src/app/core/api.service.ts` — add methods for:
  - `getSections()`, `getSectionTree(section)`, `getPage(section, path)`, `createPage(...)`, `updatePage(...)`, `deletePage(...)`, `movePage(...)`
  - `getRevisions(section, path)`, `getRevision(section, path, rev)`
  - `listAttachments(section, path)`, `uploadAttachment(section, path, file)`, `deleteAttachment(id)`
  - Remove old convention methods

### 3.2 — TypeScript Models

**Files to modify:**
- `CodeGraphWeb/src/app/core/models.ts` — add wiki interfaces (`WikiSection`, `WikiTreeNode`, `WikiPage`, `WikiPageListItem`, `WikiRevision`, `WikiAttachment`), remove convention interfaces

### 3.3 — Wiki Layout with Sidebar Navigation

The main shell: sidebar + content area.

**Files to create:**
- `CodeGraphWeb/src/app/pages/wiki/wiki-layout.component.ts` — layout shell
- `CodeGraphWeb/src/app/pages/wiki/wiki-layout.component.html` — sidebar + `<router-outlet>`
- `CodeGraphWeb/src/app/pages/wiki/wiki-sidebar.component.ts` — recursive tree component
  - Loads sections via `getSections()`
  - On section click: loads tree via `getSectionTree(section)`, collapses other sections
  - Recursive rendering of tree nodes (folders expand/collapse, pages navigate)
  - Collapsible via toggle button
  - Highlights current page in tree

### 3.4 — Section & Page Components

**Files to create:**
- `CodeGraphWeb/src/app/pages/wiki/wiki-section.component.ts` — section landing page (overview content or child list)
- `CodeGraphWeb/src/app/pages/wiki/wiki-page.component.ts` — page view: markdown rendered content, edit button (if authenticated), revision history toggle, attachment list
- `CodeGraphWeb/src/app/pages/wiki/wiki-page-edit.component.ts` — inline edit form (title, markdown textarea, save/cancel)
- `CodeGraphWeb/src/app/pages/wiki/wiki-page-new.component.ts` — create page form (title, content, parent context)
- `CodeGraphWeb/src/app/pages/wiki/wiki-revisions.component.ts` — revision list with diff or view capability

### 3.5 — Markdown Rendering Upgrade

Replace manual regex rendering with `marked` (already in package.json) + `highlight.js` (already in package.json).

**Files to create:**
- `CodeGraphWeb/src/app/shared/markdown.component.ts` — standalone component: takes markdown string input, renders HTML via `marked` with `highlight.js` for code blocks

### 3.6 — Attachment UI

**Files to modify:**
- `CodeGraphWeb/src/app/pages/wiki/wiki-page.component.ts` — add attachment list display and upload button (for authenticated users)

### 3.7 — Admin Section Management Page

**Files to create:**
- `CodeGraphWeb/src/app/pages/admin/admin-sections.component.ts` — CRUD for root sections (title, slug, description, icon, sort order, flags)

### 3.8 — Route Configuration

**Files to modify:**
- `CodeGraphWeb/src/app/app.routes.ts` — replace `/conventions/**` routes with:
  - `/wiki` → redirect to first section
  - `/wiki/:section` → `WikiSectionComponent`
  - `/wiki/:section/**` → `WikiPageComponent` (catch-all for nested paths)
  - All under `WikiLayoutComponent` parent route
- `CodeGraphWeb/src/app/app.component.ts` — update navigation links (replace "Conventions" with "Wiki")

### Phase 3 Verification

- [ ] Sidebar shows all sections; clicking expands/collapses correctly
- [ ] Page content renders markdown properly (code blocks, tables, links, headings)
- [ ] Can create pages at any valid depth (up to 4 levels)
- [ ] Can edit pages (requires sign-in; author auto-filled from OAuth)
- [ ] Can delete pages (with confirmation dialog)
- [ ] Revision history viewable for any page
- [ ] Attachments upload, display, download, and delete
- [ ] Sidebar highlights current page
- [ ] Admin section management works (create/edit/delete/reorder sections)
- [ ] Old `/conventions` URLs no longer work (or redirect to `/wiki/conventions`)
- [ ] Anonymous users see read-only view; edit buttons hidden

---

## Phase 4: MCP Auto-Generation

**Goal:** MCP tool documentation is auto-generated into wiki pages with editable manual sections.

### 4.1 — MCP Metadata Enumeration

Enumerate registered MCP tools and their metadata (name, description, parameters).

**Files to create:**
- `src/CodeGraph.Services/IMcpDocService.cs` — `RegenerateAsync()`, `GetToolMetadataAsync()`
- `src/CodeGraph.Services/McpDocService.cs` — implementation:
  - Reflects on `[McpServerTool]` attributes in `CodeGraphMcpServer` to extract tool name, description, parameters
  - Alternatively: introspect the MCP server's tool registry if the SDK exposes it

### 4.2 — Auto-Generation Logic

For each tool:
1. Find or create a `wiki_pages` entry in the MCP Documentation section with `is_auto_generated = true`
2. Build content using the `<!-- AUTO:START -->` / `<!-- AUTO:END -->` template from PRD Section 7
3. On existing pages, replace only content between markers; preserve everything else
4. Remove pages for tools that no longer exist
5. Set author to "system" for auto-generated content

**Files to modify:**
- `src/CodeGraph.Services/McpDocService.cs` — the core logic above

### 4.3 — Startup Hook & Admin Endpoint

Run auto-generation on startup and on admin request.

**Files to modify:**
- `src/CodeGraph/Startup.cs` (or `Program.cs`) — call `IMcpDocService.RegenerateAsync()` during app startup (after DB is ready)
- `src/CodeGraph/Controllers/AdminController.cs` — add `POST /api/admin/mcp/regenerate` endpoint

### 4.4 — Section Lockdown

Ensure the MCP Documentation section enforces `allow_user_pages = false` — no manual page creation or folder creation. Only auto-generated pages and admin edits to existing pages.

**Already handled in Phase 2** via the `allow_user_pages` flag check in `WikiService`. Verify it works for this section.

### Phase 4 Verification

- [ ] On startup, MCP Documentation section populated with one page per MCP tool
- [ ] Each page has `<!-- AUTO:START -->` / `<!-- AUTO:END -->` block with accurate metadata
- [ ] Manual edits outside markers survive regeneration
- [ ] Admins can trigger regeneration via button in admin operations
- [ ] Pages for removed tools are deleted on regeneration
- [ ] New tools get pages on regeneration
- [ ] Cannot create new pages in MCP Documentation section (returns 403/400)

---

## Phase 5: Polish & Initial Content

**Goal:** System is seeded, tested end-to-end, and ready for use.

### 5.1 — Seed Initial Content

Create seed data (can be a migration or admin action):
- Verify 5 root sections exist with correct flags
- Create sample skill page: parent doc + child with raw skill content + attachment
- Create sample agent page: same pattern
- Verify MCP docs auto-generated

### 5.2 — Markdown Rendering Polish

- Ensure `marked` + `highlight.js` integration handles all expected content types
- Test: code blocks (C#, TypeScript, SQL, JSON, YAML), tables, links, images, headings, lists, blockquotes
- Add CSS styling for rendered markdown (consistent with site theme)

### 5.3 — Admin Operations Page Completion

Ensure all admin operations have proper UI:
- processRepos (with repo list input)
- reIndexAll (with confirmation)
- link (with confirmation)
- discover (with optional filter)
- processBatchAnalysis (with optional repo filter)
- MCP regenerate (with confirmation)

### 5.4 — Error Handling & Edge Cases

- Handle expired tokens gracefully (redirect to login)
- Handle 403s (show "not authorized" message)
- Handle 404s for wiki pages (show "page not found" with create option if authenticated)
- Handle concurrent edits (revision mismatch → show conflict message)

### 5.5 — Remove ConventionUpdated Consumer Registration

Verify MassTransit no longer registers `ConventionUpdatedConsumer`. Clean up any lingering references.

*Should already be done in Phase 2.9, but verify as a final check.*

### Phase 5 Verification

- [ ] End-to-end: anonymous browse → sign in → create page → edit → view revision → attach file → download
- [ ] End-to-end: admin sign in → manage sections → manage users → run operations → regenerate MCP docs
- [ ] Skills pattern works: doc page → child skill content page → download attachment
- [ ] Agents pattern works: same as skills
- [ ] MCP tools `list_conventions` / `get_convention` return correct data
- [ ] All markdown renders correctly with syntax highlighting
- [ ] Token expiry handled gracefully
- [ ] No old convention routes or references remain

---

## File Change Summary

### New Files (by phase)

| Phase | Path | Description |
|-------|------|-------------|
| 1 | `sql/migrations/005_admin_foundation.sql` | admin_users + settings_overrides tables |
| 1 | `src/CodeGraph.Services/Configuration/WikiOptions.cs` | Attachment and wiki settings |
| 1 | `src/CodeGraph.Services/Configuration/AuthOptions.cs` | OAuth2/JWT config |
| 1 | `src/CodeGraph/Auth/AdminRequirement.cs` | Authorization requirement |
| 1 | `src/CodeGraph/Auth/AdminAuthorizationHandler.cs` | Checks admin_users table |
| 1 | `src/CodeGraph.Services/ISettingsService.cs` | Settings service interface |
| 1 | `src/CodeGraph.Services/SettingsService.cs` | Settings merge logic |
| 1 | `src/CodeGraph.Services/IAdminUserService.cs` | Admin user CRUD interface |
| 1 | `src/CodeGraph.Services/AdminUserService.cs` | Admin user CRUD |
| 1 | `CodeGraphWeb/src/app/core/auth.service.ts` | OAuth2 PKCE flow |
| 1 | `CodeGraphWeb/src/app/core/auth.interceptor.ts` | Bearer token interceptor |
| 1 | `CodeGraphWeb/src/app/core/admin.guard.ts` | Admin route guard |
| 1 | `CodeGraphWeb/src/app/pages/auth-callback/auth-callback.component.ts` | OAuth callback |
| 1 | `CodeGraphWeb/src/app/pages/admin/admin-layout.component.ts` | Admin shell |
| 1 | `CodeGraphWeb/src/app/pages/admin/admin-settings.component.ts` | JSON settings editor |
| 1 | `CodeGraphWeb/src/app/pages/admin/admin-users.component.ts` | Admin user management |
| 1 | `CodeGraphWeb/src/app/pages/admin/admin-operations.component.ts` | Operations UI |
| 2 | `sql/migrations/006_wiki.sql` | Wiki tables + convention migration |
| 2 | `src/CodeGraph.Models/Requests/WikiPageRequest.cs` | Page request DTO |
| 2 | `src/CodeGraph.Models/Requests/WikiSectionRequest.cs` | Section request DTO |
| 2 | `src/CodeGraph.Models/Requests/WikiPageMoveRequest.cs` | Move request DTO |
| 2 | `src/CodeGraph.Models/Responses/WikiResponses.cs` | All wiki response DTOs |
| 2 | `src/CodeGraph.Services/IWikiService.cs` | Wiki CRUD interface |
| 2 | `src/CodeGraph.Services/WikiService.cs` | Wiki business logic |
| 2 | `src/CodeGraph.Services/IAttachmentService.cs` | Attachment interface |
| 2 | `src/CodeGraph.Services/AttachmentService.cs` | Filesystem attachment logic |
| 2 | `src/CodeGraph/Controllers/WikiController.cs` | All wiki REST routes |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-layout.component.ts` | Wiki shell with sidebar |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-layout.component.html` | Layout template |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-sidebar.component.ts` | Recursive nav tree |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-section.component.ts` | Section landing |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-page.component.ts` | Page viewer |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-page-edit.component.ts` | Page editor |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-page-new.component.ts` | Page creation |
| 3 | `CodeGraphWeb/src/app/pages/wiki/wiki-revisions.component.ts` | Revision history |
| 3 | `CodeGraphWeb/src/app/shared/markdown.component.ts` | Markdown renderer |
| 3 | `CodeGraphWeb/src/app/pages/admin/admin-sections.component.ts` | Section management |
| 4 | `src/CodeGraph.Services/IMcpDocService.cs` | MCP doc gen interface |
| 4 | `src/CodeGraph.Services/McpDocService.cs` | Auto-generation logic |

### Files to Delete (Phase 2)

| Path | Reason |
|------|--------|
| `src/CodeGraph/Controllers/ConventionsController.cs` | Replaced by WikiController |
| `src/CodeGraph.Services/ConventionService.cs` | Replaced by WikiService |
| `src/CodeGraph.Services/IConventionService.cs` | Replaced by IWikiService |
| `src/CodeGraph.Models/Messages/ConventionUpdated.cs` | Event removed per PRD |
| `src/CodeGraph/Consumers/ConventionUpdatedConsumer.cs` | Consumer removed per PRD |
| Convention request/response models | Replaced by wiki models |

### Key Files Modified Across Phases

| Path | Phases | Changes |
|------|--------|---------|
| `src/CodeGraph/Startup.cs` | 1, 2, 4 | Auth, DI registrations, MCP doc startup hook |
| `src/CodeGraph.Data/Entities.cs` | 1, 2 | Add wiki entities, remove convention entities |
| `src/CodeGraph.Data/CodeGraphDbContext.cs` | 1, 2 | Add/remove DbSets |
| `src/CodeGraph/Controllers/AdminController.cs` | 1, 2, 4 | Settings, users, sections, MCP regenerate |
| `src/CodeGraph.Services/Configuration/CodeGraphServiceSettings.cs` | 1 | Add WikiOptions, AuthOptions |
| `src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs` | 2 | Repoint convention tools |
| `CodeGraphWeb/src/app/core/api.service.ts` | 1, 3 | Add wiki & admin API methods |
| `CodeGraphWeb/src/app/core/models.ts` | 3 | Add wiki models, remove convention models |
| `CodeGraphWeb/src/app/app.routes.ts` | 1, 3 | Auth callback, admin routes, wiki routes |
| `CodeGraphWeb/src/app/app.component.ts` | 1, 3 | Auth UI in header, nav link changes |
| `CodeGraphWeb/package.json` | 1 | Add oidc-client-ts |

---

## Dependency Order

```
Phase 1 ──→ Phase 2 ──→ Phase 3 ──→ Phase 5
                    └──→ Phase 4 ──→ Phase 5
```

- Phase 1 is a prerequisite for everything (auth is needed for write operations)
- Phase 2 must complete before Phase 3 (UI needs API) and Phase 4 (MCP docs need wiki tables)
- Phases 3 and 4 can be done in parallel
- Phase 5 depends on all prior phases

---

## Risk Notes

| Risk | Mitigation |
|------|------------|
| IdentityServer token claim names don't match expectations | Pre-flight SQL queries in PRD Section 16; decode JWT at jwt.io to verify claims |
| Consul config conflicts with DB overrides | Settings merge is additive; DB wins on overlap. Clear documentation in admin UI. |
| Path-based routing collisions with reserved words (`_new`, `revisions`, `attachments`) | Use `_new` as special slug (disallow in page creation); revisions/attachments are sub-routes, not page slugs |
| Large tree performance for deeply nested sections | Trees are per-section; 4-level max depth limits total nodes. Lazy-load children if needed later. |
| MCP tool reflection may not expose parameter metadata | Fallback: parse XML doc comments or hardcode tool descriptions |
