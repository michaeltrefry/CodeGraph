-- A direct dream write and its inverse commit in the same transaction as the knowledge change.
CREATE TABLE IF NOT EXISTS dream_write_journal (
 id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
 run_id CHAR(36) NOT NULL,
 project_id CHAR(36) NOT NULL,
 entry_id CHAR(36) NOT NULL,
 action VARCHAR(32) NOT NULL,
 before_json LONGTEXT NOT NULL,
 after_json LONGTEXT NOT NULL,
 evidence_json LONGTEXT NOT NULL,
 prevalence VARCHAR(1000) NOT NULL,
 created_at DATETIME(6) NOT NULL,
 reverted_at DATETIME(6) NULL,
 reverted_by VARCHAR(255) NULL,
 KEY ix_dream_journal_run (project_id, run_id, id)
) ENGINE=InnoDB;
