import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { McpPersonalAccessTokenMetadata } from '../../core/models';
import { AccessTokensComponent } from './access-tokens.component';

describe('AccessTokensComponent', () => {
  let api: {
    listMcpTokens: ReturnType<typeof vi.fn>;
    createMcpToken: ReturnType<typeof vi.fn>;
    revokeMcpToken: ReturnType<typeof vi.fn>;
  };
  let component: AccessTokensComponent;

  const token: McpPersonalAccessTokenMetadata = {
    id: 12,
    tokenName: 'desktop',
    tokenPrefix: 'cgpat_abc',
    lastFour: '9xyz',
    createdAtUtc: '2026-04-26T00:00:00Z',
    expiresAtUtc: '2026-07-26T00:00:00Z',
    status: 'active'
  };

  beforeEach(() => {
    api = {
      listMcpTokens: vi.fn().mockReturnValue(of([token])),
      createMcpToken: vi.fn().mockReturnValue(of({ token, rawToken: 'cgpat_secret' })),
      revokeMcpToken: vi.fn().mockReturnValue(of(void 0))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AccessTokensComponent());
  });

  it('loads existing tokens', async () => {
    await component.ngOnInit();

    expect(component.tokens()).toEqual([token]);
  });

  it('creates a token and reveals the raw value once', async () => {
    component.newName = 'desktop';
    component.expiresInDays = 30;

    await component.create();

    expect(api.createMcpToken).toHaveBeenCalledWith('desktop', 30);
    expect(component.rawToken()).toBe('cgpat_secret');
    expect(component.success()).toBe('Created desktop.');
  });

  it('revokes an active token', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));

    await component.revoke(token);

    expect(api.revokeMcpToken).toHaveBeenCalledWith(12);
    expect(component.success()).toBe('Revoked desktop.');
  });
});
