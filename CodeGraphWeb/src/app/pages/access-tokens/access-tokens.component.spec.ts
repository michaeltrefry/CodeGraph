import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { McpPersonalAccessTokenMetadata } from '../../core/models';
import { AccessTokensComponent } from './access-tokens.component';

describe('AccessTokensComponent', () => {
  let api: {
    listMcpTokens: ReturnType<typeof vi.fn>;
    listUserMcpTools: ReturnType<typeof vi.fn>;
    createMcpToken: ReturnType<typeof vi.fn>;
    updateMcpTokenTools: ReturnType<typeof vi.fn>;
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
    status: 'active',
    entitlementMode: 'selected',
    toolNames: ['search_graph']
  };

  beforeEach(() => {
    api = {
      listMcpTokens: vi.fn().mockReturnValue(of([token])),
      listUserMcpTools: vi.fn().mockReturnValue(of([
        {
          toolName: 'search_graph',
          providerKey: 'codegraph',
          displayName: 'Search graph',
          description: 'Search graph',
          readOnly: true,
          destructive: false,
          enabled: true,
          isAvailable: true,
          defaultSelected: true,
          accessClass: 'read',
          requiresCredential: false,
          updatedAtUtc: '2026-04-26T00:00:00Z'
        },
        {
          toolName: 'query_memory',
          providerKey: 'codegraph',
          displayName: 'Query memory',
          description: 'Query memory',
          readOnly: true,
          destructive: false,
          enabled: true,
          isAvailable: true,
          defaultSelected: true,
          accessClass: 'read',
          requiresCredential: false,
          updatedAtUtc: '2026-04-26T00:00:00Z'
        }
      ])),
      createMcpToken: vi.fn().mockReturnValue(of({ token, rawToken: 'cgpat_secret' })),
      updateMcpTokenTools: vi.fn().mockReturnValue(of({ ...token, toolNames: ['query_memory'] })),
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
    await component.ngOnInit();
    component.newName = 'desktop';
    component.expiresInDays = 30;

    await component.create();

    // The read-only preset selects every enabled read-only tool, sorted.
    expect(api.createMcpToken).toHaveBeenCalledWith('desktop', 30, ['query_memory', 'search_graph']);
    expect(component.rawToken()).toBe('cgpat_secret');
    expect(component.success()).toBe('Created desktop.');
  });

  it('revokes an active token', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));

    await component.revoke(token);

    expect(api.revokeMcpToken).toHaveBeenCalledWith(12);
    expect(component.success()).toBe('Revoked desktop.');
  });

  it('edits the tool entitlements of an active token', async () => {
    await component.ngOnInit();

    component.startEditTools(token);
    expect(component.editingTokenId()).toBe(12);
    expect([...component.editToolNames()]).toEqual(['search_graph']);

    component.toggleEditTool('search_graph', false);
    component.toggleEditTool('query_memory', true);
    await component.saveEditTools(token);

    expect(api.updateMcpTokenTools).toHaveBeenCalledWith(12, ['query_memory']);
    expect(component.editingTokenId()).toBeNull();
    expect(component.success()).toBe('Updated tool access for desktop.');
  });

  it('rejects saving an empty tool selection', async () => {
    await component.ngOnInit();
    component.startEditTools(token);
    component.toggleEditTool('search_graph', false);

    await component.saveEditTools(token);

    expect(api.updateMcpTokenTools).not.toHaveBeenCalled();
    expect(component.error()).toBe('Select at least one MCP tool.');
  });
});
