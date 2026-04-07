# PRD: AI Wiki

**Status:** Draft
**Author:** MTrefry / Claude
**Date:** 2026-03-21
**Branch:** Wiki-Features

---

## 1. Overview

Expand the existing Conventions wiki into a general-purpose **AI Wiki** — a standalone knowledge base for developers and Claude. The wiki holds multiple document types organized in a navigable tree, with downloadable skill/agent artifacts, auto-generated MCP documentation, file attachments, and admin-managed configuration.

### Goals

- Replace the single-purpose Conventions wiki with a multi-section, hierarchical wiki
- Provide installable Claude Skills and Subagent artifacts with documentation
- Auto-generate and maintain MCP tool documentation from live metadata
- Add OAuth2-based authentication for editing and admin operations
- Expose admin UI for site settings (`CodeGraphServiceSettings`) and admin operations
- Keep the system extensible — new root sections can be added by admins at any time

### Non-Goals

- Full-text search (deferred to a later version)
- Site branding customization
- Comments or discussion threads on pages
- Integration with the code graph (wiki is standalone)

---

## 2. Users & Roles

| Role | Can do |
|------|--------|
| **Anonymous** | Browse all wiki pages (read-only) |
| **User** (authenticated) | Edit any wiki page, create pages/folders within sections, upload attachments |
| **Admin** (authenticated) | Everything a User can do, plus: manage root sections, manage site settings, run admin operations, manage roles |

Author (OAuth `username` claim) is captured on every edit for tracking.

---

## 3. Authentication & Authorization

### OAuth2 Integration

- **Flow:** Authorization Code
- **Staging Environment:**
  - Authorization URL: `https://stgauth.tcdevops.com/connect/authorize`
  - Token URL: `https://stgauth.tcdevops.com/connect/token`
- **Production Environment** (future):
  - Authorization URL: `https://auth.tcdevops.com/connect/authorize`
  - Token URL: `https://auth.tcdevops.com/connect/token`
- **Scopes:** `openid`, `username`
- **Token caching:** Once obtained, reuse until expiry. Do not re-prompt.

### Where Auth Is Required

| Action | Auth Required |
|--------|--------------|
| Browse/read wiki pages | No |
| Edit/create/delete wiki pages | Yes (User or Admin) |
| Upload attachments | Yes (User or Admin) |
| Manage root sections | Yes (Admin only) |
| Access admin settings UI | Yes (Admin only) |
| Run admin operations | Yes (Admin only) |

### API Token Validation

The API must validate JWT tokens from the OAuth2 provider. Standard ASP.NET JWT bearer middleware with issuer/audience validation.

---

## 4. Information Architecture

### Hierarchy

```
AI Wiki (root)
├── General                    # Admin-created root section
│   ├── Onboarding/            # Folder (page with children)
│   │   ├── Getting Started    # Page
│   │   └── Dev Environment    # Page
│   └── Architecture Decisions # Page
├── Conventions                # Migrated from existing wiki
│   ├── API Naming             # Existing convention page
│   └── ...
├── Skills                     # Skill docs + installable artifacts
│   ├── Commit Skill/          # Folder: documentation page
│   │   └── skill.md           # Child page: raw skill content (downloadable)
│   └── ...
├── Agents                     # Same pattern as Skills
│   ├── Code Review Agent/
│   │   └── agent config       # Child page: raw agent config (downloadable)
│   └── ...
└── MCP Documentation          # Auto-generated, admin-editable overview
    ├── search_graph            # Auto-generated tool page (editable)
    ├── trace_call_path         # Auto-generated tool page (editable)
    └── ...                     # No user-created pages/folders allowed
```

### Rules

- **Root sections** are dynamic, admin-created. Starting set: General, Conventions, Skills, Agents, MCP Documentation.
- **Nesting depth:** Maximum 4 levels (section → folder → folder → folder → page).
- **Folders are pages with children.** A folder can have its own content describing its children.
- **MCP Documentation is special:** Auto-generated tool pages only. Admins can edit the root overview and individual tool pages, but no one can add new pages or sub-folders. Tool pages are created/removed automatically when tools change.
- **Skills & Agents** are regular pages. The "installable artifact" pattern is: parent page = documentation, child page = raw content. Any page can have attachments, so the downloadable file is just an attachment on the child page.

---

## 5. Data Model

### New Tables

#### `wiki_sections`

Root sections managed by admins.

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `slug` | VARCHAR(200) UNIQUE | URL segment |
| `title` | VARCHAR(500) | Display name |
| `description` | TEXT | Optional section description |
| `icon` | VARCHAR(100) | Optional icon identifier |
| `sort_order` | INT | Display ordering |
| `is_system` | BOOLEAN DEFAULT FALSE | True for MCP Docs (prevents deletion) |
| `allow_user_pages` | BOOLEAN DEFAULT TRUE | False for MCP Docs (prevents user-created pages) |
| `created_at` | DATETIME | |
| `updated_at` | DATETIME | |

#### `wiki_pages`

Replaces `convention_pages`. Hierarchical pages within sections.

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `section_id` | BIGINT FK → wiki_sections | |
| `parent_id` | BIGINT FK → wiki_pages (nullable) | NULL = top-level in section |
| `slug` | VARCHAR(200) | Unique within parent scope |
| `title` | VARCHAR(500) | |
| `content` | MEDIUMTEXT | Markdown |
| `author` | VARCHAR(200) | OAuth username of last editor |
| `revision` | INT DEFAULT 1 | |
| `sort_order` | INT DEFAULT 0 | Ordering among siblings |
| `is_auto_generated` | BOOLEAN DEFAULT FALSE | True for MCP tool pages |
| `depth` | INT DEFAULT 0 | 0-3, enforced in app logic |
| `created_at` | DATETIME | |
| `updated_at` | DATETIME | |

**Constraints:**
- UNIQUE(`section_id`, `parent_id`, `slug`) — slugs unique among siblings
- FK `parent_id` CASCADE DELETE — deleting a folder deletes children
- FK `section_id` CASCADE DELETE — deleting a section deletes all pages

#### `wiki_revisions`

Replaces `convention_revisions`. Same full-snapshot approach.

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `page_id` | BIGINT FK → wiki_pages CASCADE | |
| `revision` | INT | |
| `title` | VARCHAR(500) | |
| `content` | MEDIUMTEXT | |
| `author` | VARCHAR(200) | |
| `created_at` | DATETIME | |

**Constraints:**
- UNIQUE(`page_id`, `revision`)

#### `wiki_attachments`

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `page_id` | BIGINT FK → wiki_pages CASCADE | |
| `filename` | VARCHAR(500) | Original filename |
| `storage_path` | VARCHAR(1000) | Path on disk |
| `content_type` | VARCHAR(200) | MIME type |
| `size_bytes` | BIGINT | |
| `uploaded_by` | VARCHAR(200) | OAuth username |
| `created_at` | DATETIME | |

#### `admin_users`

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `username` | VARCHAR(200) UNIQUE | OAuth username |
| `created_at` | DATETIME | |

Simple list of admin usernames. Any authenticated user not in this table is a regular user. Bootstrap by manually inserting the first admin.

#### `settings_overrides`

| Column | Type | Notes |
|--------|------|-------|
| `id` | BIGINT AUTO_INCREMENT PK | |
| `settings_json` | MEDIUMTEXT | Full JSON of CodeGraphServiceSettings overrides |
| `updated_by` | VARCHAR(200) | OAuth username |
| `updated_at` | DATETIME | |

Single-row table. Stores admin-edited settings as JSON. Merged over the bound `CodeGraphServiceSettings` at runtime. Only one row exists (upsert on save).

### Tables to Drop

- `convention_pages` (after data migration)
- `convention_revisions` (after data migration)

---

## 6. API Design

### Wiki Pages — `/api/wiki`

| Verb | Route | Auth | Description |
|------|-------|------|-------------|
| GET | `/api/wiki/sections` | None | List all root sections |
| GET | `/api/wiki/{section}/tree` | None | Full navigation tree for a section (id, slug, title, children, depth) |
| GET | `/api/wiki/{section}/{*path}` | None | Get page by section + path (e.g., `/api/wiki/skills/commit-skill/skill-md`) |
| POST | `/api/wiki/{section}` | User | Create page in section (body: title, content, parentId?) |
| POST | `/api/wiki/{section}/{*path}` | User | Create child page under path |
| PUT | `/api/wiki/{section}/{*path}` | User | Update page |
| DELETE | `/api/wiki/{section}/{*path}` | User | Delete page (cascades to children) |
| GET | `/api/wiki/{section}/{*path}/revisions` | None | List revisions |
| GET | `/api/wiki/{section}/{*path}/revisions/{rev}` | None | Get specific revision |
| PATCH | `/api/wiki/{section}/{*path}/move` | User | Move page (change parent, section, or sort order) |

### Attachments — `/api/wiki/.../attachments`

| Verb | Route | Auth | Description |
|------|-------|------|-------------|
| GET | `/api/wiki/{section}/{*path}/attachments` | None | List attachments for a page |
| POST | `/api/wiki/{section}/{*path}/attachments` | User | Upload attachment (multipart) |
| GET | `/api/wiki/attachments/{id}/{filename}` | None | Download attachment |
| DELETE | `/api/wiki/attachments/{id}` | User | Delete attachment |

### Admin — `/api/admin`

Existing admin endpoints remain. New additions:

| Verb | Route | Auth | Description |
|------|-------|------|-------------|
| GET | `/api/admin/sections` | Admin | List sections (with management metadata) |
| POST | `/api/admin/sections` | Admin | Create root section |
| PUT | `/api/admin/sections/{id}` | Admin | Update section (title, sort order, etc.) |
| DELETE | `/api/admin/sections/{id}` | Admin | Delete section (cascades) — blocked for system sections |
| GET | `/api/admin/settings` | Admin | Get current `CodeGraphServiceSettings` as JSON (excluding ConnectionString) |
| PUT | `/api/admin/settings` | Admin | Update settings (JSON merge) |
| GET | `/api/admin/admins` | Admin | List admin usernames |
| POST | `/api/admin/admins` | Admin | Add admin username |
| DELETE | `/api/admin/admins/{username}` | Admin | Remove admin username |
| POST | `/api/admin/mcp/regenerate` | Admin | Regenerate MCP documentation pages from current tool metadata |

### MCP Tools (unchanged names)

| Tool | Change |
|------|--------|
| `list_conventions` | Repointed to query `wiki_pages` WHERE section slug = `conventions` |
| `get_convention` | Repointed to query `wiki_pages` by slug within conventions section |

---

## 7. MCP Documentation Auto-Generation

### Trigger

- On application startup
- On admin request (`POST /api/admin/mcp/regenerate`)

### Behavior

1. Enumerate all registered MCP tools and their metadata (name, description, parameters, parameter descriptions).
2. For each tool, find or create a `wiki_pages` entry in the MCP Documentation section with `is_auto_generated = true`.
3. **Preserve manual edits:** Page content uses a structured format with delimited metadata sections. On regeneration, only the metadata placeholders are replaced; any content outside the delimiters is preserved.
4. Remove pages for tools that no longer exist.
5. The root MCP Documentation page is admin-editable for overview content.

### Content Template

```markdown
<!-- AUTO:START - Do not edit between these markers -->
## {tool_name}

**Description:** {description}

### Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| {name} | {type} | {required} | {description} |
<!-- AUTO:END -->

{manual content preserved here}
```

---

## 8. Angular Frontend

### Navigation

- **Left sidebar** with collapsible navigation tree, visible by default.
- Tree shows all root sections. Only the currently browsed section is expanded.
- Clicking a section expands it and collapses others.
- Folders expand/collapse on click. Pages navigate on click.
- Sidebar can be collapsed/expanded via toggle button.

### URL Structure

```
/wiki                              → Redirect to first section
/wiki/:section                     → Section root (list or section overview page)
/wiki/:section/:slug               → Page within section
/wiki/:section/:slug/:slug         → Nested page (up to 4 levels)
/wiki/:section/:slug/:slug/:slug
/wiki/:section/:slug/:slug/:slug/:slug
```

### Pages & Components

| Component | Route | Description |
|-----------|-------|-------------|
| `WikiLayoutComponent` | `/wiki/**` | Shell with sidebar tree + content area |
| `WikiSectionComponent` | `/wiki/:section` | Section landing — overview or page list |
| `WikiPageComponent` | `/wiki/:section/{*path}` | View page with markdown, edit toggle, revision history, attachments |
| `WikiPageEditComponent` | Inline or modal | Edit form (title, content as markdown, author auto-filled from OAuth) |
| `WikiNewPageComponent` | `/wiki/:section/.../_new` | Create page within current folder |
| `AdminSettingsComponent` | `/admin/settings` | JSON editor for `CodeGraphServiceSettings` |
| `AdminSectionsComponent` | `/admin/sections` | CRUD for root sections (title, slug, sort order, icon, flags) |
| `AdminUsersComponent` | `/admin/users` | List users, assign admin/user role |
| `AdminOperationsComponent` | `/admin/operations` | Buttons/forms for existing admin endpoints (processRepos, reIndexAll, link, discover, processBatchAnalysis, mcp/regenerate) |

### Markdown Rendering

Upgrade from current basic regex rendering to a proper library (e.g., `ngx-markdown` or `marked`). Must support code blocks with syntax highlighting, tables, and links.

### Auth Flow (Angular)

1. User clicks "Sign In" (or is redirected when attempting an edit).
2. Angular redirects to OAuth2 authorization URL with PKCE.
3. On callback, exchange code for token at token URL.
4. Store token in memory (or sessionStorage). Attach as `Authorization: Bearer {token}` on protected API calls.
5. Do not re-prompt until token expires.
6. Show username in header when authenticated. Show "Sign In" when not.

---

## 9. Admin Section

### Settings Editor (`/admin/settings`)

- Fetches current `CodeGraphServiceSettings` as JSON from `GET /api/admin/settings`.
- Displays in a JSON editor (e.g., `ngx-json-editor` or `monaco-editor` with JSON mode).
- **ConnectionString is excluded** from both GET and PUT — never exposed to the UI.
- Save sends `PUT /api/admin/settings` with the full JSON. API validates and applies.
- Settings are persisted to the `settings_overrides` DB table and merged over the bound `CodeGraphServiceSettings` at runtime, while still allowing runtime changes through the UI.

### Section Management (`/admin/sections`)

- List all root sections with drag-to-reorder or sort order input.
- Create new section: title, slug (auto-generated), description, icon, `allow_user_pages` toggle.
- Edit existing section properties.
- Delete section (with confirmation, cascades all pages). Blocked for system sections (`is_system = true`).

### Admin Management (`/admin/admins`)

- `admin_users` table is a simple list of usernames with admin access.
- Admin UI lists current admins, allows adding/removing usernames.
- Bootstrap: manually insert the first admin username into the table (no auto-promotion).
- Any authenticated user not in `admin_users` is treated as a regular user.

### Operations (`/admin/operations`)

- UI wrappers for existing `AdminController` endpoints.
- Each operation has a button and optional parameters (e.g., repo filter for processRepos).
- Shows response/status after execution.
- Add "Regenerate MCP Docs" button.

---

## 10. File Attachments

### Storage

- Files stored on local filesystem under a configurable directory (default: `uploads/wiki/`).
- Path structure: `uploads/wiki/{page_id}/{filename}` — simple, avoids collisions.
- File size limit: **10 MB default**, configurable via admin settings (new setting: `WikiOptions.MaxAttachmentSizeMb`).

### Upload Flow

1. User selects file on wiki page.
2. `POST /api/wiki/{section}/{*path}/attachments` with `multipart/form-data`.
3. API validates size, stores file, creates `wiki_attachments` record.
4. Returns attachment metadata (id, filename, URL, size).

### Download

- `GET /api/wiki/attachments/{id}/{filename}` — serves file with correct `Content-Type`.
- The `{filename}` segment is for human-readable URLs and `Content-Disposition`.

### Deletion

- Deleting an attachment removes both the DB record and the file on disk.
- Deleting a page cascades to delete all its attachments. File cleanup is synchronous but best-effort — filesystem errors are logged, not thrown. Orphaned files can be cleaned up later.

---

## 11. Data Migration

### Convention Pages → Wiki Pages

1. Create the "Conventions" root section in `wiki_sections`.
2. For each row in `convention_pages`, insert into `wiki_pages` with `section_id` = Conventions, `parent_id` = NULL, same slug/title/content/author/revision/timestamps.
3. For each row in `convention_revisions`, insert into `wiki_revisions` with the new `page_id`.
4. Drop `convention_pages` and `convention_revisions` tables.

This is a SQL migration script (`005_wiki.sql` or similar).

---

## 12. Configuration Additions

New settings added to `CodeGraphServiceSettings`:

```csharp
public class WikiOptions
{
    public int MaxAttachmentSizeMb { get; set; } = 10;
    public string AttachmentStoragePath { get; set; } = "uploads/wiki";
}

public class AuthOptions
{
    public string AuthorizationUrl { get; set; }
    public string TokenUrl { get; set; }
    public string ClientId { get; set; }
    public string Authority { get; set; }  // For JWT validation
}
```

---

## 13. Backward Compatibility

### Breaking Changes (acceptable — nothing in production)

- `/api/conventions/*` routes removed. Replaced by `/api/wiki/conventions/*`.
- `convention_pages` and `convention_revisions` tables dropped after migration.
- Angular routes change from `/conventions/*` to `/wiki/conventions/*`.

### Preserved

- MCP tool names `list_conventions` and `get_convention` unchanged. Internal queries repointed.

### Removed

- `ConventionUpdated` MassTransit event and any consumer — no external consumers exist and there's no use case for a `WikiPageUpdated` event.

---

## 14. Implementation Phases

### Phase 1: Auth & Admin Foundation

- OAuth2 integration (Angular + API JWT validation)
- `admin_users` table and role middleware
- `settings_overrides` table
- Admin settings page (JSON editor for `CodeGraphServiceSettings`)
- Admin management page (add/remove admin usernames)
- Admin operations page (wrappers for existing `AdminController` endpoints)

### Phase 2: Wiki Data Model & API

- New tables: `wiki_sections`, `wiki_pages`, `wiki_revisions`, `wiki_attachments`
- Migration script including convention data migration
- `IWikiService` with full CRUD for sections, pages, revisions
- `WikiController` with all routes from Section 6
- Repoint `list_conventions` / `get_convention` MCP tools
- File attachment upload/download/delete

### Phase 3: Angular Wiki UI

- `WikiLayoutComponent` with collapsible sidebar navigation tree
- Section and page browsing (read-only)
- Page create/edit with markdown editor
- Revision history viewing
- Attachment upload/download UI
- Admin section management page

### Phase 4: MCP Auto-Generation

- MCP tool metadata enumeration
- Auto-generate wiki pages with preservable manual sections
- Regeneration endpoint and admin button
- Lock down MCP section (no user-created pages)

### Phase 5: Polish & Initial Content

- Seed initial root sections (General, Conventions, Skills, Agents, MCP Documentation)
- Migrate any existing convention pages
- Populate MCP documentation from live tools
- Create sample skill and agent pages as templates
- Markdown rendering upgrade (syntax highlighting, tables)

---

## 15. Resolved Decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Settings persistence | DB table (`settings_overrides`). JSON merged over the bound `CodeGraphServiceSettings` at runtime. |
| 2 | Attachment cleanup on page delete | Synchronous, best-effort. Log errors but don't block deletion. Orphaned files can be cleaned later. |
| 3 | Admin bootstrap | `admin_users` table with manual insert. No auto-promotion. |
| 4 | ConventionUpdated event | Remove entirely. No use case for WikiPageUpdated either. |

---

## 16. IdentityServer Client Setup (Direct DB)

The OAuth2 provider is IdentityServer (hosted at `stgauth.tcdevops.com` for staging). Client registration is done via direct SQL inserts into the IdentityServer database.

### Pre-flight: Check Existing State

Run these first to understand what's already configured:

```sql
-- Check if 'username' scope already exists as an identity resource
SELECT * FROM identity_resources WHERE name = 'username';

-- Check what claim types the 'username' scope maps to (if it exists)
SELECT ir.name, irc.type
FROM identity_resources ir
JOIN identity_resource_claims irc ON irc.identity_resource_id = ir.id
WHERE ir.name = 'username';

-- Check if 'openid' scope exists (it should)
SELECT * FROM identity_resources WHERE name = 'openid';

-- See existing clients for reference on column patterns
SELECT id, client_id, client_name, enabled, require_client_secret,
       require_pkce, allow_access_tokens_via_browser, access_token_lifetime
FROM clients
LIMIT 5;
```

### Step 1: Create the Client

```sql
INSERT INTO clients (
    -- identity
    client_id, client_name, description, enabled, protocol_type,
    -- secrets & pkce
    require_client_secret, require_pkce, allow_plain_text_pkce, require_request_object,
    -- consent
    require_consent, allow_remember_consent,
    -- tokens
    always_include_user_claims_in_id_token, allow_access_tokens_via_browser,
    allow_offline_access, access_token_type, include_jwt_id,
    access_token_lifetime, identity_token_lifetime, authorization_code_lifetime,
    -- refresh tokens (not used, but NOT NULL)
    absolute_refresh_token_lifetime, sliding_refresh_token_lifetime,
    refresh_token_usage, refresh_token_expiration, update_access_token_claims_on_refresh,
    -- logout (not used, but NOT NULL)
    front_channel_logout_session_required, back_channel_logout_session_required,
    -- claims
    always_send_client_claims,
    -- login & device
    enable_local_login, device_code_lifetime, non_editable,
    -- timestamps
    created
) VALUES (
    -- identity
    'codegraph-web', 'CodeGraph Web', 'CodeGraph Angular SPA - AI Wiki and admin', 1, 'oidc',
    -- secrets & pkce
    0,          -- require_client_secret = false (public SPA client)
    1,          -- require_pkce = true
    0,          -- allow_plain_text_pkce = false (require S256)
    0,          -- require_request_object = false
    -- consent
    0,          -- require_consent = false
    1,          -- allow_remember_consent = true
    -- tokens
    0,          -- always_include_user_claims_in_id_token = false
    1,          -- allow_access_tokens_via_browser = true
    0,          -- allow_offline_access = false
    0,          -- access_token_type = 0 (JWT)
    0,          -- include_jwt_id = false
    3600,       -- access_token_lifetime = 1 hour
    300,        -- identity_token_lifetime = 5 min
    300,        -- authorization_code_lifetime = 5 min
    -- refresh tokens (defaults, not used since allow_offline_access = false)
    2592000,    -- absolute_refresh_token_lifetime = 30 days
    1296000,    -- sliding_refresh_token_lifetime = 15 days
    1,          -- refresh_token_usage = 1 (one-time)
    1,          -- refresh_token_expiration = 1 (sliding)
    0,          -- update_access_token_claims_on_refresh = false
    -- logout
    1,          -- front_channel_logout_session_required = true
    1,          -- back_channel_logout_session_required = true
    -- claims
    0,          -- always_send_client_claims = false
    -- login & device
    1,          -- enable_local_login = true
    300,        -- device_code_lifetime = 5 min
    0,          -- non_editable = false
    -- timestamps
    NOW()
);

-- Capture the new client's id for subsequent inserts
SET @clientId = LAST_INSERT_ID();
```

### Step 2: Grant Types

```sql
INSERT INTO client_grant_types (client_id, grant_type) VALUES
(@clientId, 'authorization_code');
```

### Step 3: Scopes

```sql
INSERT INTO client_scopes (client_id, scope) VALUES
(@clientId, 'openid'),
(@clientId, 'username');
```

### Step 4: Redirect URIs

```sql
-- Development
INSERT INTO client_redirect_uris (client_id, redirect_uri) VALUES
(@clientId, 'http://localhost:4200/auth/callback');

INSERT INTO client_post_logout_redirect_uris (client_id, post_logout_redirect_uri) VALUES
(@clientId, 'http://localhost:4200');

-- Staging (add when you have the URL)
-- INSERT INTO client_redirect_uris (client_id, redirect_uri) VALUES
-- (@clientId, 'https://codegraph.stg.tcdevops.com/auth/callback');
```

### Step 5: CORS Origins

```sql
INSERT INTO client_cors_origins (client_id, origin) VALUES
(@clientId, 'http://localhost:4200');

-- Staging (add when you have the URL)
-- INSERT INTO client_cors_origins (client_id, origin) VALUES
-- (@clientId, 'https://codegraph.stg.tcdevops.com');
```

### Step 6: Username Identity Resource (if it doesn't exist)

Only run this if the pre-flight check showed `username` doesn't exist:

```sql
-- Create the 'username' identity resource (like 'openid' or 'profile')
INSERT INTO identity_resources (
    enabled, name, display_name, description,
    required, emphasize, show_in_discovery_document,
    created, updated, non_editable
) VALUES (
    1, 'username', 'Username', 'Your username',
    0, 0, 1,
    NOW(), NOW(), 0
);

SET @usernameResourceId = LAST_INSERT_ID();

-- Map it to the actual claim type(s) in the token
-- Try 'preferred_username' first — that's the OIDC standard claim.
-- If your IdentityServer uses 'username' as the claim type instead, adjust.
INSERT INTO identity_resource_claims (identity_resource_id, type) VALUES
(@usernameResourceId, 'preferred_username');

-- If you also want the plain 'username' claim:
-- INSERT INTO identity_resource_claims (identity_resource_id, type) VALUES
-- (@usernameResourceId, 'username');
```

### Step 7: API Resource (for audience validation)

This is optional but recommended — it lets the API validate the `aud` claim in the JWT:

```sql
-- Create API resource
INSERT INTO api_resources (
    enabled, name, display_name, description,
    show_in_discovery_document, created, updated, non_editable
) VALUES (
    1, 'codegraph-api', 'CodeGraph API', 'CodeGraph API resource',
    1, NOW(), NOW(), 0
);

SET @apiResourceId = LAST_INSERT_ID();

-- Create API scope
INSERT INTO api_scopes (
    enabled, name, display_name, description,
    required, emphasized, show_in_discovery_document
) VALUES (
    1, 'codegraph-api', 'CodeGraph API', 'Access to CodeGraph API',
    0, 0, 1
);

SET @apiScopeId = LAST_INSERT_ID();

-- Link API scope to API resource
INSERT INTO api_resource_scopes (api_resource_id, scope) VALUES
(@apiResourceId, 'codegraph-api');

-- Add the API scope to the client
INSERT INTO client_scopes (client_id, scope) VALUES
(@clientId, 'codegraph-api');
```

> **Note on audience:** If you skip Step 7, the JWT won't have an `aud` claim for `codegraph-api`. In that case, disable audience validation in the API's JWT bearer config (`ValidateAudience = false`). You can always add it later.

### Verification

After inserting, verify everything is linked:

```sql
SELECT c.client_id, c.client_name, c.enabled, c.require_client_secret, c.require_pkce
FROM clients c WHERE c.client_id = 'codegraph-web';

SELECT cgt.grant_type FROM client_grant_types cgt
JOIN clients c ON c.id = cgt.client_id WHERE c.client_id = 'codegraph-web';

SELECT cs.scope FROM client_scopes cs
JOIN clients c ON c.id = cs.client_id WHERE c.client_id = 'codegraph-web';

SELECT cru.redirect_uri FROM client_redirect_uris cru
JOIN clients c ON c.id = cru.client_id WHERE c.client_id = 'codegraph-web';

SELECT cco.origin FROM client_cors_origins cco
JOIN clients c ON c.id = cco.client_id WHERE c.client_id = 'codegraph-web';
```

### What Goes in CodeGraph Configuration

```csharp
public class AuthOptions
{
    // Used by Angular (sent to browser for OIDC flow)
    public string AuthorizationUrl { get; set; }  // https://stgauth.tcdevops.com/connect/authorize
    public string TokenUrl { get; set; }           // https://stgauth.tcdevops.com/connect/token
    public string ClientId { get; set; }           // codegraph-web

    // Used by API (JWT validation via OIDC discovery)
    public string Authority { get; set; }          // https://stgauth.tcdevops.com
}
```

The API uses `Authority` to fetch `{Authority}/.well-known/openid-configuration` for signing keys and issuer validation.

### ASP.NET API Setup (JWT Bearer)

```csharp
services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = authOptions.Authority;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,                          // false if you skipped Step 7
            ValidAudiences = new[] { "codegraph-api" }
        };
    });
```

### Angular Setup

Use `angular-auth-oidc-client` or `oidc-client-ts` library. Configure with:
- Authority: `https://stgauth.tcdevops.com`
- Client ID: `codegraph-web`
- Redirect URI: `http://localhost:4200/auth/callback`
- Scope: `openid username codegraph-api` (drop `codegraph-api` if you skipped Step 7)
- Response type: `code` (authorization code + PKCE)

### Troubleshooting

- **"invalid_client"**: Client doesn't exist or `client_id` is wrong. Check `SELECT * FROM clients WHERE client_id = 'codegraph-web'`.
- **"invalid_scope"**: A requested scope isn't in `client_scopes` or doesn't exist as an `identity_resources`/`api_scopes` row.
- **CORS errors on token endpoint**: `client_cors_origins` is missing your Angular origin.
- **No `username` in token**: Check `identity_resource_claims` — the claim `type` must match what IdentityServer actually stores. Decode your JWT at jwt.io to see what claims are present.
- **`aud` claim missing**: You need an API Resource + API Scope (Step 7), or disable audience validation.
- **"redirect_uri_mismatch"**: The redirect URI in Angular must exactly match a `client_redirect_uris` entry (including trailing slashes, http vs https).
