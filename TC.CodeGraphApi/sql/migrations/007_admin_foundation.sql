-- 007_admin_foundation.sql
-- Phase 1: Admin users table and settings overrides for AI Wiki

CREATE TABLE IF NOT EXISTS admin_users (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(200) NOT NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_admin_users_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS settings_overrides (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    settings_json MEDIUMTEXT NOT NULL,
    updated_by VARCHAR(200) NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Seed initial admin
INSERT INTO admin_users (username) VALUES ('mtrefry');
