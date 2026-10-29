-- Durable messages with item threads and handover (sc-27871, epic 27864 "CodeGraph agent coordination").
--
-- A message is data: who said what to whom about what. CodeGraph stores it, delivers it and keeps
-- its state; it enforces no norms (§5.5). Three tables, none with a foreign key: rows outlive the
-- sessions and items they mention, and a foreign key would take a shared row lock on a parent that
-- another writer holds, which with the named lock below could deadlock where InnoDB cannot see it.
--
-- Ordering. work_messages.sequence, work_message_deliveries.sequence and work_message_events.sequence
-- are AUTO_INCREMENT, and every INSERT into them and the COMMIT run while the writer holds the named
-- lock 'codegraph_work_messages' (scoped to the database like the change-feed lock, see
-- MySqlWorkStore.ServerLockName). A higher sequence is therefore always committed later, and a
-- reader's cursor never skips a row. Lock order: row locks of work_items / work_projects (in id
-- order), then the messages lock, then the change-feed lock; code holding the messages lock takes no
-- further row lock on work_items, work_projects or work_sessions. A post to an item's thread holds the
-- item's row (shared; exclusive when it opens or closes a blocking question) before the lock.

CREATE TABLE IF NOT EXISTS work_messages (
    sequence BIGINT NOT NULL AUTO_INCREMENT,
    id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    -- Where the message was posted: item (the item's thread, the default for a message about an
    -- item), project, feature, resource, session (direct) or human.
    channel VARCHAR(16) NOT NULL,
    item_id CHAR(36) NULL,
    feature_id CHAR(36) NULL,
    resource_name VARCHAR(200) NULL,
    to_session_id VARCHAR(64) NULL,
    to_human TINYINT(1) NOT NULL DEFAULT 0,
    kind VARCHAR(16) NOT NULL,
    -- open / answered / declined for a question or a request; NULL for any other kind.
    state VARCHAR(16) NULL,
    -- A question to the human about an item that makes the item derived-Blocked while it is open.
    blocking TINYINT(1) NOT NULL DEFAULT 0,
    in_reply_to CHAR(36) NULL,
    body MEDIUMTEXT NOT NULL,
    sender_name VARCHAR(255) NOT NULL,
    sender_kind VARCHAR(16) NOT NULL,
    sender_session_id VARCHAR(64) NULL,
    resolved_by VARCHAR(255) NULL,
    resolved_by_kind VARCHAR(16) NULL,
    resolved_by_session_id VARCHAR(64) NULL,
    resolved_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (sequence),
    UNIQUE KEY ux_work_messages_id (id),
    KEY ix_work_messages_item (item_id, state, blocking, sequence),
    KEY ix_work_messages_project (project_id, sequence),
    KEY ix_work_messages_session (to_session_id, sequence)
) ENGINE=InnoDB;

-- One row per recipient of a message: the inbox of a session (recipient = the session id) or of the
-- project's humans (recipient = 'human'), and its replay cursor (sequence). State per recipient:
-- pending (stored, not yet returned), delivered (returned by inbox or a briefing), read (ack),
-- answered / declined (the recipient resolved the question or request). handed_over marks a
-- delivery made to the next holder of an item, or to the next session of a project and repository,
-- for a message first sent to someone else; handed_to marks the source row of a direct message
-- passed on, so that exactly one later session inherits it.
CREATE TABLE IF NOT EXISTS work_message_deliveries (
    sequence BIGINT NOT NULL AUTO_INCREMENT,
    message_id CHAR(36) NOT NULL,
    project_id CHAR(36) NOT NULL,
    recipient VARCHAR(64) NOT NULL,
    state VARCHAR(16) NOT NULL DEFAULT 'pending',
    delivered_at DATETIME(6) NULL,
    read_at DATETIME(6) NULL,
    resolved_at DATETIME(6) NULL,
    handed_over TINYINT(1) NOT NULL DEFAULT 0,
    handed_to VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (sequence),
    UNIQUE KEY ux_work_message_deliveries_recipient (message_id, recipient),
    -- The unread count of every work-tool response and the unread replay: an index range.
    KEY ix_work_message_deliveries_unread (recipient, project_id, read_at, sequence)
) ENGINE=InnoDB;

-- What the UI follows (server-sent events): a message posted, handed over, read, answered or declined.
-- Never updated or deleted.
CREATE TABLE IF NOT EXISTS work_message_events (
    sequence BIGINT NOT NULL AUTO_INCREMENT,
    project_id CHAR(36) NOT NULL,
    message_id CHAR(36) NOT NULL,
    kind VARCHAR(16) NOT NULL,
    recipient VARCHAR(64) NULL,
    actor_name VARCHAR(255) NOT NULL,
    actor_kind VARCHAR(16) NOT NULL,
    actor_session_id VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (sequence),
    KEY ix_work_message_events_project (project_id, sequence)
) ENGINE=InnoDB;
