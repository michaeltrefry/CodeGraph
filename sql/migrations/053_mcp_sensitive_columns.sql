CREATE TABLE IF NOT EXISTS mcp_sensitive_columns (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    source_key VARCHAR(64) NOT NULL DEFAULT '*',
    table_name VARCHAR(255) NOT NULL DEFAULT '*',
    column_name VARCHAR(255) NOT NULL,
    reason VARCHAR(255) NULL,
    allowed BOOLEAN NOT NULL DEFAULT FALSE,
    is_manual BOOLEAN NOT NULL DEFAULT FALSE,
    created_at_utc DATETIME(3) NOT NULL,
    updated_at_utc DATETIME(3) NOT NULL,
    UNIQUE KEY ux_mcp_sensitive_columns (source_key, table_name, column_name),
    INDEX ix_mcp_sensitive_columns_column (column_name)
) ENGINE=InnoDB;
