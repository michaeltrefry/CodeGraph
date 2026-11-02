import { describe, expect, it } from 'vitest';
import { actorLabel, actorTitle } from './actor-label';

describe('actorLabel', () => {
  it('names the owner and the system plainly', () => {
    expect(actorLabel({ name: 'michael', kind: 'user' })).toBe('michael');
    expect(actorLabel({ name: 'dreaming', kind: 'system' })).toBe('dreaming');
    expect(actorLabel(undefined)).toBe('unknown');
  });

  it('keeps the legacy session label of harness / model @ host', () => {
    expect(actorLabel({ name: 'michael', kind: 'session', harness: 'claude-code', model: 'opus', host: 'dev-mac' })).toBe('claude-code / opus @ dev-mac');
    expect(actorLabel({ name: 'bot', kind: 'session', harness: 'codex' })).toBe('codex');
    expect(actorLabel({ name: 'bot', kind: 'session' })).toBe('bot');
  });

  it('marks an agent as an agent, with its session when it wrote in one', () => {
    expect(actorLabel({ name: 'codex', kind: 'agent' })).toBe('codex (agent)');
    expect(actorLabel({ name: 'codex', kind: 'agent', session_id: 's-1', harness: 'codex', model: 'gpt-5.6', host: 'host' }))
      .toBe('codex (agent) · codex / gpt-5.6 @ host');
  });

  it('leaves out the harness, model or host an agent session lacks', () => {
    expect(actorLabel({ name: 'codex', kind: 'agent', harness: 'codex' })).toBe('codex (agent) · codex');
    expect(actorLabel({ name: 'codex', kind: 'agent', model: 'gpt-5.6', host: 'host' })).toBe('codex (agent) · gpt-5.6 @ host');
    expect(actorLabel({ name: 'codex', kind: 'agent', host: 'host' })).toBe('codex (agent) @ host');
  });
});

describe('actorTitle', () => {
  it('names the identity and, for a session, its id', () => {
    expect(actorTitle({ name: 'codex', kind: 'agent', session_id: 's-1' })).toBe('codex (session s-1)');
    expect(actorTitle({ name: 'michael', kind: 'user' })).toBe('michael');
    expect(actorTitle(null)).toBe('');
  });
});
