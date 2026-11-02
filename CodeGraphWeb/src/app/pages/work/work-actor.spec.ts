import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ActorComponent } from './work-shared';

function render(actor: Record<string, unknown>): { text: string; title: string; agent: boolean } {
  const fixture = TestBed.createComponent(ActorComponent);
  fixture.componentRef.setInput('actor', actor);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const span = host.querySelector('.wrk-actor') as HTMLElement;
  return {
    text: [host.querySelector('.wrk-actor-name'), host.querySelector('.wrk-actor-detail')]
      .map(part => part?.textContent?.trim()).filter(Boolean).join(' | '),
    title: span.getAttribute('title') ?? '',
    agent: span.classList.contains('wrk-actor-agent')
  };
}

describe('ActorComponent', () => {
  it('shows an agent as an agent, with its session detail', () => {
    const shown = render({ name: 'codex', kind: 'agent', session_id: 's-1', harness: 'codex', model: 'gpt-5.6', host: 'host' });
    expect(shown.text).toBe('codex (agent) | codex / gpt-5.6 @ host');
    expect(shown.agent).toBe(true);
    expect(shown.title).toBe('Agent codex (codex / gpt-5.6 @ host) · session s-1');
  });

  it('leaves the owner and a legacy session as they were', () => {
    expect(render({ name: 'michael', kind: 'user' })).toMatchObject({ text: 'michael', agent: false });
    expect(render({ name: 'michael', kind: 'session', harness: 'claude-code', model: 'opus' }))
      .toMatchObject({ text: 'michael | claude-code / opus', agent: true });
  });
});
