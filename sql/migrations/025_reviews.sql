-- Review workflow tables: project diagnostics, repository review runs, findings, project sections

CREATE TABLE IF NOT EXISTS project_diagnostics (
    project         VARCHAR(255) NOT NULL,
    dotnet_project  VARCHAR(255),
    source          VARCHAR(50)  NOT NULL DEFAULT 'roslyn',
    diagnostic_key  VARCHAR(255) NOT NULL,
    diagnostic_id   VARCHAR(50)  NOT NULL,
    severity        VARCHAR(20)  NOT NULL,
    message         TEXT         NOT NULL,
    category        VARCHAR(100),
    file_path       TEXT         NOT NULL,
    line_start      INT,
    line_end        INT,
    computed_at     DATETIME(3)  NOT NULL,
    PRIMARY KEY (project, diagnostic_key),
    INDEX ix_project_diagnostics_project_severity (project, dotnet_project, severity)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS repository_review_runs (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    repo                    VARCHAR(255)  NOT NULL,
    reviewed_commit_sha     VARCHAR(40),
    baseline_review_run_id  BIGINT,
    baseline_commit_sha     VARCHAR(40),
    status                  VARCHAR(32)   NOT NULL DEFAULT 'queued',
    review_mode             VARCHAR(32)   NOT NULL DEFAULT 'full',
    prompt_version          VARCHAR(10)   NOT NULL DEFAULT 'v1',
    overview_json           JSON,
    model_used              VARCHAR(100),
    created_at              DATETIME(3)   NOT NULL,
    started_at              DATETIME(3),
    completed_at            DATETIME(3),
    error                   TEXT,
    INDEX ix_repo_review_runs_repo_created (repo, created_at),
    INDEX ix_repo_review_runs_repo_sha_created (repo, reviewed_commit_sha, created_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS repository_review_project_sections (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    review_run_id       BIGINT        NOT NULL,
    project_name        VARCHAR(255)  NOT NULL,
    overview            TEXT          NOT NULL,
    strengths_json      JSON          NOT NULL,
    reviewed_areas_json JSON          NOT NULL,
    skipped_areas_json  JSON          NOT NULL,
    follow_ups_json     JSON          NOT NULL,
    reused_from_baseline TINYINT(1)   NOT NULL DEFAULT 0,
    INDEX ix_repo_review_sections_run_project (review_run_id, project_name)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS repository_review_findings (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    review_run_id           BIGINT        NOT NULL,
    project_name            VARCHAR(255),
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
    INDEX ix_repo_review_findings_run_ordinal (review_run_id, ordinal)
) ENGINE=InnoDB;
