CREATE TABLE IF NOT EXISTS memory_tenant_ownership (
    username VARCHAR(255) NOT NULL,
    ownership_status VARCHAR(32) NOT NULL,
    owner_username VARCHAR(255) NULL,
    policy VARCHAR(1024) NOT NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (username)
);

ALTER TABLE memory_write_receipts
    DROP INDEX IF EXISTS uq_memory_write_receipts_receipt_id;

CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_write_receipts_username_receipt_id
    ON memory_write_receipts (username, receipt_id);

INSERT INTO memory_tenant_ownership (username, ownership_status, owner_username, policy)
VALUES (
    'default',
    'quarantined',
    NULL,
    'Legacy shared memory is not inherited by any authenticated user. Only an audited Admin operation may inspect or delete it.'
)
ON DUPLICATE KEY UPDATE
    ownership_status = VALUES(ownership_status),
    owner_username = VALUES(owner_username),
    policy = VALUES(policy);

CREATE TABLE IF NOT EXISTS memory_admin_audit (
    id BIGINT NOT NULL AUTO_INCREMENT,
    correlation_id CHAR(32) NOT NULL,
    actor_username VARCHAR(255) NOT NULL,
    target_username VARCHAR(255) NOT NULL,
    operation VARCHAR(128) NOT NULL,
    dry_run TINYINT(1) NOT NULL DEFAULT 0,
    outcome_status VARCHAR(32) NOT NULL DEFAULT 'pending',
    succeeded TINYINT(1) NULL,
    error_type VARCHAR(255) NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_memory_admin_audit_correlation (correlation_id),
    KEY ix_memory_admin_audit_actor_created (actor_username, created_at),
    KEY ix_memory_admin_audit_target_created (target_username, created_at)
);
