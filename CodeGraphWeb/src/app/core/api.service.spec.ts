import { HttpClient } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';

describe('ApiService streaming requests', () => {
  let auth: { getValidAccessToken: ReturnType<typeof vi.fn> };
  let api: ApiService;

  beforeEach(() => {
    auth = {
      getValidAccessToken: vi.fn()
    };

    const injector = Injector.create({
      providers: [
        ApiService,
        { provide: AuthService, useValue: auth },
        { provide: HttpClient, useValue: {} }
      ]
    });

    api = runInInjectionContext(injector, () => injector.get(ApiService));
  });

  it('attaches the OAuth bearer token to Ask streaming fetches', async () => {
    auth.getValidAccessToken.mockResolvedValue('token-123');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      body: null
    }));

    const generator = api.ask('Where is indexing handled?');
    await generator.next();

    expect(fetch).toHaveBeenCalledWith(
      'http://localhost:5037/api/ask',
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          'Content-Type': 'application/json',
          Authorization: 'Bearer token-123'
        })
      }));
  });

  it('attaches the OAuth bearer token to review SSE fetches', async () => {
    auth.getValidAccessToken.mockResolvedValue('token-456');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      body: null
    }));

    const generator = api.streamRepositoryReview('CodeGraph', 42);
    await expect(generator.next()).rejects.toThrow('HTTP 401');

    expect(fetch).toHaveBeenCalledWith(
      'http://localhost:5037/api/repositories/CodeGraph/code-review/42/stream',
      expect.objectContaining({
        method: 'GET',
        headers: expect.objectContaining({
          Accept: 'text/event-stream',
          Authorization: 'Bearer token-456'
        })
      }));
  });
});

describe('ApiService job queue requests', () => {
  it('uses the admin queue URLs and posts an empty dismissal body', () => {
    const http = { get: vi.fn(), post: vi.fn() };
    const injector = Injector.create({ providers: [ApiService, { provide: AuthService, useValue: {} }, { provide: HttpClient, useValue: http }] });
    const api = runInInjectionContext(injector, () => injector.get(ApiService));
    api.getJobQueue();
    api.dismissJob('scheduled-job', 12);
    expect(http.get).toHaveBeenCalledWith('http://localhost:5037/api/admin/jobs/queue');
    expect(http.post).toHaveBeenCalledWith('http://localhost:5037/api/admin/jobs/queue/scheduled-job/12/dismiss', {});
    injector.destroy();
  });
});

describe('ApiService agent endpoints', () => {
  const http = {
    get: vi.fn().mockReturnValue('get'),
    post: vi.fn().mockReturnValue('post'),
    put: vi.fn().mockReturnValue('put'),
    patch: vi.fn().mockReturnValue('patch'),
    delete: vi.fn().mockReturnValue('delete')
  };
  const injector = Injector.create({
    providers: [
      ApiService,
      { provide: AuthService, useValue: {} },
      { provide: HttpClient, useValue: http }
    ]
  });
  const api = runInInjectionContext(injector, () => injector.get(ApiService));
  const base = 'http://localhost:5037/api/admin/agents';

  it('calls the admin agent routes', () => {
    api.listAgents();
    expect(http.get).toHaveBeenLastCalledWith(base);
    api.createAgent('codex');
    expect(http.post).toHaveBeenLastCalledWith(base, { name: 'codex' });
    api.updateAgent(7, { disabled: true });
    expect(http.patch).toHaveBeenLastCalledWith(`${base}/7`, { disabled: true });
    api.deleteAgent(7);
    expect(http.delete).toHaveBeenLastCalledWith(`${base}/7`);
  });

  it('calls the admin agent token routes', () => {
    api.listAgentTokens(7);
    expect(http.get).toHaveBeenLastCalledWith(`${base}/7/tokens`);
    api.createAgentToken(7, 'host', 30);
    expect(http.post).toHaveBeenLastCalledWith(`${base}/7/tokens`, { name: 'host', expiresInDays: 30, toolNames: undefined });
    api.updateAgentTokenTools(7, 12, ['a']);
    expect(http.put).toHaveBeenLastCalledWith(`${base}/7/tokens/12/tools`, { toolNames: ['a'] });
    api.revokeAgentToken(7, 12);
    expect(http.delete).toHaveBeenLastCalledWith(`${base}/7/tokens/12`);
  });
});
