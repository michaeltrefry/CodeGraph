-- Project scoping for memory (CG-156, feature CG-F17): which work projects (073 work_projects.project_key)
-- a memory entity or claim belongs to, so a project's Memory tab lists only that project's memory.
--
-- A join table, not a column: one entity (a shared library, a person) or claim can belong to several
-- projects, and every write or backfill only adds a (row, project) pair. Memory stays per user: a scope
-- row hangs off an entity or claim row that already carries its username, and is deleted with it.
-- origin says how the scope was decided: write (the writing session's project, or an explicit project
-- on the write) or backfill (this migration's name match). A later write upgrades backfill to write.
--
-- Rows are keyed by project_key (it cannot be changed) and not foreign-keyed to work_projects: a
-- deleted project leaves rows nothing asks for.

CREATE TABLE IF NOT EXISTS memory_entity_projects (
    entity_id BIGINT NOT NULL,
    project_key VARCHAR(10) NOT NULL,
    origin VARCHAR(16) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    PRIMARY KEY (entity_id, project_key),
    KEY ix_memory_entity_projects_project (project_key, entity_id),
    CONSTRAINT fk_memory_entity_projects_entity FOREIGN KEY (entity_id) REFERENCES memory_entities_v2(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS memory_claim_projects (
    claim_id BIGINT NOT NULL,
    project_key VARCHAR(10) NOT NULL,
    origin VARCHAR(16) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    PRIMARY KEY (claim_id, project_key),
    KEY ix_memory_claim_projects_project (project_key, claim_id),
    CONSTRAINT fk_memory_claim_projects_claim FOREIGN KEY (claim_id) REFERENCES memory_claims(id) ON DELETE CASCADE
) ENGINE=InnoDB;

-- One-time backfill of memory written before scoping existed. A text names a repository when the
-- repository's name (any member of a project: owned or dependency) is one whole token of it, ignoring
-- case. A token is a maximal run of the characters a repository name is made of (letters, digits, '.',
-- '_', '-'); anything else separates tokens. So "github.com/x/CodeGraph/pull/3" and "CodeGraph#12" name
-- CodeGraph, while "CodeGraph.Api" names neither CodeGraph nor Api. Each statement only adds missing
-- pairs, so a replay changes nothing.

-- Entities whose label names a member repository.
INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
SELECT DISTINCT entity.id, project.project_key, 'backfill', UTC_TIMESTAMP(3)
FROM memory_entities_v2 entity
JOIN work_project_repositories member
  ON LOCATE(CONCAT(' ', LOWER(member.repository_name), ' '),
            CONCAT(' ', REGEXP_REPLACE(LOWER(entity.label), '[^a-z0-9._-]+', ' '), ' ')) > 0
JOIN work_projects project ON project.id = member.project_id
ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin;

-- Claims with evidence whose source_ref names a member repository.
INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
SELECT DISTINCT claim.id, project.project_key, 'backfill', UTC_TIMESTAMP(3)
FROM memory_evidence evidence
JOIN memory_claims claim ON claim.id = evidence.claim_id
JOIN work_project_repositories member
  ON LOCATE(CONCAT(' ', LOWER(member.repository_name), ' '),
            CONCAT(' ', REGEXP_REPLACE(LOWER(evidence.source_ref), '[^a-z0-9._-]+', ' '), ' ')) > 0
JOIN work_projects project ON project.id = member.project_id
ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin;

-- Claims about an entity (subject or object) whose label names a member repository. Matched on the
-- label itself, not on the scopes above, so a replay cannot widen the result transitively.
INSERT INTO memory_claim_projects (claim_id, project_key, origin, created_at)
SELECT DISTINCT claim.id, project.project_key, 'backfill', UTC_TIMESTAMP(3)
FROM memory_claims claim
JOIN memory_entities_v2 entity
  ON entity.id = claim.subject_entity_id OR entity.id = claim.object_entity_id
JOIN work_project_repositories member
  ON LOCATE(CONCAT(' ', LOWER(member.repository_name), ' '),
            CONCAT(' ', REGEXP_REPLACE(LOWER(entity.label), '[^a-z0-9._-]+', ' '), ' ')) > 0
JOIN work_projects project ON project.id = member.project_id
ON DUPLICATE KEY UPDATE memory_claim_projects.origin = memory_claim_projects.origin;

-- Entities a scoped claim is about (its subject and object), as a write scopes them.
INSERT INTO memory_entity_projects (entity_id, project_key, origin, created_at)
SELECT DISTINCT entity.id, scope.project_key, 'backfill', UTC_TIMESTAMP(3)
FROM memory_claim_projects scope
JOIN memory_claims claim ON claim.id = scope.claim_id
JOIN memory_entities_v2 entity
  ON entity.id = claim.subject_entity_id OR entity.id = claim.object_entity_id
ON DUPLICATE KEY UPDATE memory_entity_projects.origin = memory_entity_projects.origin;
