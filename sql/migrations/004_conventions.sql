-- Convention pages: simple wiki for team conventions/standards
CREATE TABLE convention_pages (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    slug VARCHAR(200) NOT NULL,
    title VARCHAR(500) NOT NULL,
    content MEDIUMTEXT NOT NULL,
    author VARCHAR(200) NOT NULL,
    revision INT NOT NULL DEFAULT 1,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_convention_slug (slug)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Revision history for convention pages
CREATE TABLE convention_revisions (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    page_id BIGINT NOT NULL,
    revision INT NOT NULL,
    title VARCHAR(500) NOT NULL,
    content MEDIUMTEXT NOT NULL,
    author VARCHAR(200) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_revision_page FOREIGN KEY (page_id) REFERENCES convention_pages(id) ON DELETE CASCADE,
    UNIQUE KEY uq_revision (page_id, revision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
