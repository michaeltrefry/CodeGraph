-- Add raw content support to wiki sections and pages
-- Sections with has_raw_content=true show a second editor for raw file content (e.g., Skills, Agents)

ALTER TABLE wiki_sections
    ADD COLUMN has_raw_content BOOLEAN NOT NULL DEFAULT FALSE AFTER allow_user_pages;

-- Update Skills and Agents sections to enable raw content
UPDATE wiki_sections SET has_raw_content = TRUE WHERE slug IN ('skills', 'agents');

ALTER TABLE wiki_pages
    ADD COLUMN raw_content MEDIUMTEXT NULL AFTER content;

ALTER TABLE wiki_revisions
    ADD COLUMN raw_content MEDIUMTEXT NULL AFTER content;
