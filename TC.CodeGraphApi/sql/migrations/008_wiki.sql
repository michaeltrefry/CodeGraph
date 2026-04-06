-- 008_wiki.sql
-- Phase 2: Wiki tables, convention data migration, drop old tables

-- Wiki sections (admin-managed root categories)
CREATE TABLE IF NOT EXISTS wiki_sections (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    slug VARCHAR(200) NOT NULL,
    title VARCHAR(500) NOT NULL,
    description TEXT,
    icon VARCHAR(100),
    sort_order INT NOT NULL DEFAULT 0,
    is_system BOOLEAN NOT NULL DEFAULT FALSE,
    allow_user_pages BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_wiki_sections_slug (slug)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Wiki pages (hierarchical, replaces convention_pages)
CREATE TABLE IF NOT EXISTS wiki_pages (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    section_id BIGINT NOT NULL,
    parent_id BIGINT,
    slug VARCHAR(200) NOT NULL,
    title VARCHAR(500) NOT NULL,
    content MEDIUMTEXT NOT NULL,
    author VARCHAR(200) NOT NULL,
    revision INT NOT NULL DEFAULT 1,
    sort_order INT NOT NULL DEFAULT 0,
    is_auto_generated BOOLEAN NOT NULL DEFAULT FALSE,
    depth INT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_wiki_pages_sibling_slug (section_id, parent_id, slug),
    CONSTRAINT fk_wiki_pages_section FOREIGN KEY (section_id) REFERENCES wiki_sections(id) ON DELETE CASCADE,
    CONSTRAINT fk_wiki_pages_parent FOREIGN KEY (parent_id) REFERENCES wiki_pages(id) ON DELETE CASCADE,
    INDEX idx_wiki_pages_section (section_id),
    INDEX idx_wiki_pages_parent (parent_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Wiki revisions (full snapshot per edit, replaces convention_revisions)
CREATE TABLE IF NOT EXISTS wiki_revisions (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    page_id BIGINT NOT NULL,
    revision INT NOT NULL,
    title VARCHAR(500) NOT NULL,
    content MEDIUMTEXT NOT NULL,
    author VARCHAR(200) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_wiki_revisions_page_rev (page_id, revision),
    CONSTRAINT fk_wiki_revisions_page FOREIGN KEY (page_id) REFERENCES wiki_pages(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Wiki attachments
CREATE TABLE IF NOT EXISTS wiki_attachments (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    page_id BIGINT NOT NULL,
    filename VARCHAR(500) NOT NULL,
    storage_path VARCHAR(1000) NOT NULL,
    content_type VARCHAR(200) NOT NULL,
    size_bytes BIGINT NOT NULL,
    uploaded_by VARCHAR(200) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_wiki_attachments_page FOREIGN KEY (page_id) REFERENCES wiki_pages(id) ON DELETE CASCADE,
    INDEX idx_wiki_attachments_page (page_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Seed root sections
INSERT INTO wiki_sections (slug, title, description, icon, sort_order, is_system, allow_user_pages) VALUES
('general', 'General', 'General documentation and guides', 'book', 0, FALSE, TRUE),
('conventions', 'Conventions', 'Team conventions and coding standards', 'scale', 1, FALSE, TRUE),
('skills', 'Skills', 'Claude Code skills with installable artifacts', 'zap', 2, FALSE, TRUE),
('agents', 'Agents', 'Claude Code subagent configurations', 'bot', 3, FALSE, TRUE),
('mcp-documentation', 'MCP Documentation', 'Auto-generated MCP tool documentation', 'cpu', 4, TRUE, FALSE);

-- Migrate convention_pages → wiki_pages
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT
    (SELECT id FROM wiki_sections WHERE slug = 'conventions'),
    NULL,
    cp.slug,
    cp.title,
    cp.content,
    cp.author,
    cp.revision,
    0,
    FALSE,
    0,
    cp.created_at,
    cp.updated_at
FROM convention_pages cp;

-- Migrate convention_revisions → wiki_revisions
INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT
    wp.id,
    cr.revision,
    cr.title,
    cr.content,
    cr.author,
    cr.created_at
FROM convention_revisions cr
JOIN convention_pages cp ON cp.id = cr.page_id
JOIN wiki_pages wp ON wp.slug = cp.slug
    AND wp.section_id = (SELECT id FROM wiki_sections WHERE slug = 'conventions');

-- Drop old tables
DROP TABLE IF EXISTS convention_revisions;
DROP TABLE IF EXISTS convention_pages;
