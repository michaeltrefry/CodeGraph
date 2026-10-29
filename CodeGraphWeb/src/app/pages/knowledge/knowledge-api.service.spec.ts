import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { firstValueFrom, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { environment } from '../../../environments/environment';
import { KnowledgeApiService } from './knowledge-api.service';
import { IdempotencyKey, actorLabel, errorText, workError } from './knowledge-ui';
import { AGENT, HUMAN } from './knowledge-fixtures';

const base = `${environment.apiUrl}/work`;

function service() {
  const http = {
    post: vi.fn().mockReturnValue(of({ items: [{ id: 'p', key: 'CG', kind: 'project', title: 'CodeGraph' }] })),
    get: vi.fn().mockReturnValue(of({ items: [{ id: 'e-1' }] }))
  };
  const injector = Injector.create({ providers: [{ provide: HttpClient, useValue: http }] });
  return { http, api: runInInjectionContext(injector, () => new KnowledgeApiService()) };
}

describe('KnowledgeApiService', () => {
  it('uses the agents\' work routes for every call', async () => {
    const { http, api } = service();

    expect(await firstValueFrom(api.listProjects())).toEqual([{ id: 'p', key: 'CG', kind: 'project', title: 'CodeGraph' }]);
    expect(http.post).toHaveBeenLastCalledWith(`${base}/find`, { kind: 'project', limit: 200 });

    await firstValueFrom(api.recall({ project: 'CG', kind: ['decision'] }));
    expect(http.post).toHaveBeenLastCalledWith(`${base}/knowledge/recall`, { project: 'CG', kind: ['decision'] });

    await firstValueFrom(api.record({ retract: { id: 'e-1', expected_version: 3, reason: 'wrong' } }));
    expect(http.post).toHaveBeenLastCalledWith(`${base}/knowledge`, { retract: { id: 'e-1', expected_version: 3, reason: 'wrong' } });

    await firstValueFrom(api.promote('l-1', { expected_version: 2, action: 'approve' }));
    expect(http.post).toHaveBeenLastCalledWith(`${base}/knowledge/l-1/promote`, { expected_version: 2, action: 'approve' });

    await firstValueFrom(api.challenge({ resolve: { challenge_id: 'c', expected_version: 5, outcome: 'uphold', reason: 'ok' } }));
    expect(http.post).toHaveBeenLastCalledWith(`${base}/knowledge/challenges`, { resolve: { challenge_id: 'c', expected_version: 5, outcome: 'uphold', reason: 'ok' } });

    await firstValueFrom(api.lessons({ project: 'CG', view: 'recently_learned' }));
    expect(http.post).toHaveBeenLastCalledWith(`${base}/knowledge/lessons`, { project: 'CG', view: 'recently_learned' });
  });

  it('reads one entry in full and the review queue by query', async () => {
    const { http, api } = service();

    expect(await firstValueFrom(api.getEntry('e-1'))).toEqual({ id: 'e-1' });
    const [url, options] = http.get.mock.calls[0] as [string, { params: HttpParams }];
    expect(url).toBe(`${base}/knowledge/e-1`);
    expect(options.params.toString()).toBe('detail=full');

    await firstValueFrom(api.reviewQueue('CG', 20, 'next'));
    const [queueUrl, queueOptions] = http.get.mock.calls[1] as [string, { params: HttpParams }];
    expect(queueUrl).toBe(`${base}/knowledge/review-queue`);
    expect(queueOptions.params.toString()).toBe('project=CG&limit=20&cursor=next');
  });
});

describe('knowledge UI helpers', () => {
  it('renders a person by name and a session as harness / model @ host', () => {
    expect(actorLabel(HUMAN)).toBe('michael');
    expect(actorLabel(AGENT)).toBe('claude-code / opus @ dev-mac');
    expect(actorLabel({ name: 'bot', kind: 'session', harness: 'codex' })).toBe('codex');
    expect(actorLabel(undefined)).toBe('unknown');
  });

  it('keeps an idempotency key until reset', () => {
    const key = new IdempotencyKey();
    const first = key.value;
    expect(first).toMatch(/^ui-[\x21-\x7e]{1,125}$/);
    expect(key.value).toBe(first);
    key.reset();
    expect(key.value).not.toBe(first);
  });

  it('reads the work error envelope', () => {
    const conflict = new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Version 4 is current.' } });
    expect(workError(conflict).code).toBe('conflict');
    expect(errorText(conflict)).toContain('reloaded');
    const invalid = new HttpErrorResponse({ status: 400, error: { code: 'invalid_argument', message: 'reason is required.' } });
    expect(errorText(invalid)).toBe('reason is required.');
  });
});
