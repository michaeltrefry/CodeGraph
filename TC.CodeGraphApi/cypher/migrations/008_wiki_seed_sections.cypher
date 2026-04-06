// Seed default wiki sections (equivalent to MySQL migration 008_wiki.sql seed data)
// Uses IdCounter to assign sequential appId values matching the Neo4j WikiStore pattern.

// General
MERGE (c:IdCounter {label: 'WikiSection'})
ON CREATE SET c.current = 1
ON MATCH SET c.current = c.current + 1
WITH c.current AS id
MERGE (s:WikiSection {slug: 'general'})
ON CREATE SET
    s.appId = id,
    s.title = 'General',
    s.description = 'General documentation and guides',
    s.icon = 'book',
    s.sortOrder = 0,
    s.isSystem = false,
    s.allowUserPages = true,
    s.hasRawContent = false,
    s.createdAt = datetime(),
    s.updatedAt = datetime();

// Conventions
MERGE (c:IdCounter {label: 'WikiSection'})
ON CREATE SET c.current = 1
ON MATCH SET c.current = c.current + 1
WITH c.current AS id
MERGE (s:WikiSection {slug: 'conventions'})
ON CREATE SET
    s.appId = id,
    s.title = 'Conventions',
    s.description = 'Team conventions and coding standards',
    s.icon = 'scale',
    s.sortOrder = 1,
    s.isSystem = false,
    s.allowUserPages = true,
    s.hasRawContent = false,
    s.createdAt = datetime(),
    s.updatedAt = datetime();

// Skills
MERGE (c:IdCounter {label: 'WikiSection'})
ON CREATE SET c.current = 1
ON MATCH SET c.current = c.current + 1
WITH c.current AS id
MERGE (s:WikiSection {slug: 'skills'})
ON CREATE SET
    s.appId = id,
    s.title = 'Skills',
    s.description = 'Claude Code skills with installable artifacts',
    s.icon = 'zap',
    s.sortOrder = 2,
    s.isSystem = false,
    s.allowUserPages = true,
    s.hasRawContent = false,
    s.createdAt = datetime(),
    s.updatedAt = datetime();

// Agents
MERGE (c:IdCounter {label: 'WikiSection'})
ON CREATE SET c.current = 1
ON MATCH SET c.current = c.current + 1
WITH c.current AS id
MERGE (s:WikiSection {slug: 'agents'})
ON CREATE SET
    s.appId = id,
    s.title = 'Agents',
    s.description = 'Claude Code subagent configurations',
    s.icon = 'bot',
    s.sortOrder = 3,
    s.isSystem = false,
    s.allowUserPages = true,
    s.hasRawContent = false,
    s.createdAt = datetime(),
    s.updatedAt = datetime();

// MCP Documentation (system-managed, no user pages)
MERGE (c:IdCounter {label: 'WikiSection'})
ON CREATE SET c.current = 1
ON MATCH SET c.current = c.current + 1
WITH c.current AS id
MERGE (s:WikiSection {slug: 'mcp-documentation'})
ON CREATE SET
    s.appId = id,
    s.title = 'MCP Documentation',
    s.description = 'Auto-generated MCP tool documentation',
    s.icon = 'cpu',
    s.sortOrder = 4,
    s.isSystem = true,
    s.allowUserPages = false,
    s.hasRawContent = false,
    s.createdAt = datetime(),
    s.updatedAt = datetime();
