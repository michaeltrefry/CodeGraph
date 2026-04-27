CREATE TABLE IF NOT EXISTS project_review_runs (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    project             VARCHAR(255)  NOT NULL,
    project_name        VARCHAR(255)  NOT NULL,
    reviewed_commit_sha VARCHAR(40),
    status              VARCHAR(32)   NOT NULL DEFAULT 'queued',
    review_mode         VARCHAR(32)   NOT NULL DEFAULT 'standard',
    prompt_version      VARCHAR(10)   NOT NULL DEFAULT 'v1',
    overview_json       JSON,
    model_used          VARCHAR(100),
    created_at          DATETIME(3)   NOT NULL,
    started_at          DATETIME(3),
    completed_at        DATETIME(3),
    error               TEXT,
    INDEX ix_project_review_runs_project_name_created (project, project_name, created_at),
    INDEX ix_project_review_runs_project_sha_created (project, project_name, reviewed_commit_sha, created_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS project_review_findings (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    review_run_id           BIGINT        NOT NULL,
    ordinal                 INT           NOT NULL DEFAULT 0,
    severity                VARCHAR(20)   NOT NULL,
    category                VARCHAR(50)   NOT NULL,
    title                   VARCHAR(500)  NOT NULL,
    explanation             TEXT          NOT NULL,
    evidence                TEXT          NOT NULL,
    file_path               TEXT          NOT NULL,
    line_start              INT,
    line_end                INT,
    suggested_improvement   TEXT          NOT NULL DEFAULT '',
    confidence              VARCHAR(20)   NOT NULL DEFAULT 'medium',
    provenance_json         JSON,
    INDEX ix_project_review_findings_run_ordinal (review_run_id, ordinal)
) ENGINE=InnoDB;
