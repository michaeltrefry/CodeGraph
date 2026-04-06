// Wiki section, page, revision, and attachment constraints and indexes

// Sections
CREATE CONSTRAINT wiki_section_slug IF NOT EXISTS
FOR (s:WikiSection) REQUIRE s.slug IS UNIQUE;

CREATE INDEX wiki_section_appid IF NOT EXISTS
FOR (s:WikiSection) ON (s.appId);

// Pages
CREATE INDEX wiki_page_appid IF NOT EXISTS
FOR (p:WikiPage) ON (p.appId);

CREATE INDEX wiki_page_section_slug IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.slug);

CREATE INDEX wiki_page_section_parent IF NOT EXISTS
FOR (p:WikiPage) ON (p.sectionId, p.parentId);

// Revisions
CREATE INDEX wiki_revision_appid IF NOT EXISTS
FOR (r:WikiRevision) ON (r.appId);

CREATE INDEX wiki_revision_page IF NOT EXISTS
FOR (r:WikiRevision) ON (r.pageId, r.revision);

// Attachments
CREATE INDEX wiki_attachment_appid IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.appId);

CREATE INDEX wiki_attachment_page IF NOT EXISTS
FOR (a:WikiAttachment) ON (a.pageId);

// Admin users
CREATE CONSTRAINT admin_user_username IF NOT EXISTS
FOR (a:AdminUser) REQUIRE a.username IS UNIQUE;

CREATE INDEX admin_user_appid IF NOT EXISTS
FOR (a:AdminUser) ON (a.appId);

// Settings overrides
CREATE INDEX settings_override_appid IF NOT EXISTS
FOR (s:SettingsOverride) ON (s.appId);

// ID counters
CREATE CONSTRAINT id_counter_label IF NOT EXISTS
FOR (c:IdCounter) REQUIRE c.label IS UNIQUE
