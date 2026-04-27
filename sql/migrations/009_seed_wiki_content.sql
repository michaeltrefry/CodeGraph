-- Migration 009: Seed initial wiki content
-- Phase 5.1 - Sample content for Skills and Agents sections

-- ── Skills section: sample skill page with child ──
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT s.id, NULL, 'writing-skills', 'Writing Claude Code Skills',
'This guide covers how to create effective Claude Code skills (SKILL.md files) that extend Claude''s capabilities within a project.

## What is a Skill?

A skill is a markdown file (typically `SKILL.md`) that provides Claude Code with specialized instructions for a specific task. When invoked via a slash command, the skill''s content is injected into the conversation as context.

## Skill Structure

Every skill file follows this pattern:

```yaml
---
name: my-skill
description: Short description shown in /help
---
```

Followed by the prompt content that Claude receives when the skill is invoked.

## Best Practices

- **Be specific** — tell Claude exactly what to do, not just what the concept is
- **Include examples** — show the expected output format
- **Set constraints** — define what Claude should NOT do
- **Use XML tags** — structure complex instructions with tags like `<rules>`, `<examples>`

## See Also

Check the child pages below for concrete skill examples used in this project.',
'system', 1, 0, FALSE, 0, NOW(), NOW()
FROM wiki_sections s WHERE s.slug = 'skills';

-- Create revision for the skills parent page
INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT p.id, 1, p.title, p.content, 'system', NOW()
FROM wiki_pages p
JOIN wiki_sections s ON p.section_id = s.id
WHERE s.slug = 'skills' AND p.slug = 'writing-skills' AND p.parent_id IS NULL;

-- Child page: commit skill example
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT s.id, parent.id, 'commit-skill-example', 'Example: Commit Skill',
'# Commit Skill

This is an example of a well-structured skill that handles git commits.

## Skill Content

```yaml
---
name: commit
description: Create a well-formatted git commit
---
```

```markdown
Review all staged and unstaged changes, then create a commit following these rules:

1. Run `git diff --cached` and `git diff` to see all changes
2. Write a commit message that:
   - Starts with a verb (Add, Fix, Update, Remove, Refactor)
   - Summarizes the "why" not the "what"
   - Stays under 72 characters for the subject line
3. Stage relevant files (avoid .env, credentials, large binaries)
4. Create the commit
```

## Why This Works

- Clear step-by-step instructions
- Explicit constraints (72 chars, verb-first)
- Safety guardrails (skip sensitive files)',
'system', 1, 1, FALSE, 1, NOW(), NOW()
FROM wiki_sections s
JOIN wiki_pages parent ON parent.section_id = s.id AND parent.slug = 'writing-skills' AND parent.parent_id IS NULL
WHERE s.slug = 'skills';

INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT p.id, 1, p.title, p.content, 'system', NOW()
FROM wiki_pages p
JOIN wiki_pages parent ON p.parent_id = parent.id
JOIN wiki_sections s ON p.section_id = s.id
WHERE s.slug = 'skills' AND p.slug = 'commit-skill-example';

-- ── Agents section: sample agent page with child ──
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT s.id, NULL, 'writing-agents', 'Writing Claude Code Subagents',
'This guide covers how to create specialized subagents that Claude Code can spawn for parallel or isolated work.

## What is a Subagent?

A subagent is a separate Claude instance launched by the Agent tool. Each subagent gets its own context window and can work independently, returning results to the parent conversation.

## When to Use Subagents

- **Parallel research** — search multiple areas of the codebase simultaneously
- **Isolated execution** — run risky operations in a worktree without affecting the main branch
- **Specialized roles** — reviewers, testers, planners each with focused instructions

## Agent Configuration

Subagents are defined as markdown files that specify:

```yaml
---
name: my-agent
description: What this agent does (shown to the orchestrator)
model: sonnet  # optional model override
tools:         # restrict available tools
  - Read
  - Grep
  - Glob
---
```

## Best Practices

- **Limit tools** — only give agents the tools they actually need
- **Define the role clearly** — agents work best with a focused, specific purpose
- **Return structured output** — tell the agent what format to return results in
- **Use background mode** — for truly independent work, run agents in background',
'system', 1, 0, FALSE, 0, NOW(), NOW()
FROM wiki_sections s WHERE s.slug = 'agents';

INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT p.id, 1, p.title, p.content, 'system', NOW()
FROM wiki_pages p
JOIN wiki_sections s ON p.section_id = s.id
WHERE s.slug = 'agents' AND p.slug = 'writing-agents' AND p.parent_id IS NULL;

-- Child page: explore agent example
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT s.id, parent.id, 'explore-agent-example', 'Example: Explore Agent',
'# Explore Agent

A fast, read-only agent optimized for codebase exploration.

## Configuration

```yaml
---
name: Explore
description: Fast agent for exploring codebases
model: sonnet
tools:
  - Read
  - Grep
  - Glob
  - Bash
---
```

## Prompt Template

```markdown
Search the codebase to answer the following question:

{user_query}

Be thorough but efficient. Use Glob to find files by pattern,
Grep to search content, and Read to examine specific files.
Return a structured summary of your findings.
```

## Design Decisions

- **Read-only tools** — cannot accidentally modify code
- **Sonnet model** — faster and cheaper for search tasks
- **No Agent tool** — prevents recursive agent spawning',
'system', 1, 1, FALSE, 1, NOW(), NOW()
FROM wiki_sections s
JOIN wiki_pages parent ON parent.section_id = s.id AND parent.slug = 'writing-agents' AND parent.parent_id IS NULL
WHERE s.slug = 'agents';

INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT p.id, 1, p.title, p.content, 'system', NOW()
FROM wiki_pages p
JOIN wiki_pages parent ON p.parent_id = parent.id
JOIN wiki_sections s ON p.section_id = s.id
WHERE s.slug = 'agents' AND p.slug = 'explore-agent-example';

-- ── General section: welcome page ──
INSERT INTO wiki_pages (section_id, parent_id, slug, title, content, author, revision, sort_order, is_auto_generated, depth, created_at, updated_at)
SELECT s.id, NULL, 'welcome', 'Welcome to CodeGraph Wiki',
'# Welcome to CodeGraph Wiki

This wiki is the central knowledge base for the CodeGraph platform. It contains documentation on team conventions, skills, agents, and auto-generated MCP tool references.

## Sections

| Section | Description |
|---------|-------------|
| **General** | General documentation and guides |
| **Conventions** | Team coding conventions and standards |
| **Skills** | Claude Code skill definitions and examples |
| **Agents** | Subagent configurations and patterns |
| **MCP Documentation** | Auto-generated docs for MCP tools (read-only) |

## How to Contribute

1. **Sign in** using the button in the top-right corner
2. Navigate to the section you want to edit
3. Click **Edit** on any page, or **+ New Page** to create one
4. Write content in **Markdown** — code blocks, tables, and images are all supported

> **Note:** The MCP Documentation section is auto-generated and cannot have user-created pages. You can still edit the manual sections of auto-generated pages.',
'system', 1, 0, FALSE, 0, NOW(), NOW()
FROM wiki_sections s WHERE s.slug = 'general';

INSERT INTO wiki_revisions (page_id, revision, title, content, author, created_at)
SELECT p.id, 1, p.title, p.content, 'system', NOW()
FROM wiki_pages p
JOIN wiki_sections s ON p.section_id = s.id
WHERE s.slug = 'general' AND p.slug = 'welcome' AND p.parent_id IS NULL;
