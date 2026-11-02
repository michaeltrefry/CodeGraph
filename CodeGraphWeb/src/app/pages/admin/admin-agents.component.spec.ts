import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AgentSummary, McpPersonalAccessTokenMetadata } from '../../core/models';
import { AdminAgentsComponent } from './admin-agents.component';

const codex: AgentSummary = {
  id: 7, name: 'codex', status: 'active', createdBy: 'michael',
  createdAtUtc: '2026-11-01T00:00:00Z', updatedAtUtc: '2026-11-01T00:00:00Z', disabledAtUtc: null, activeTokens: 1
};
const token: McpPersonalAccessTokenMetadata = {
  id: 12, tokenName: 'build host', tokenPrefix: 'cgpat_abc', lastFour: '9xyz',
  createdAtUtc: '2026-11-01T00:00:00Z', expiresAtUtc: '2027-01-30T00:00:00Z',
  status: 'active', entitlementMode: 'all', toolNames: []
};
const tool = (toolName: string, readOnly: boolean) => ({
  toolName, providerKey: 'codegraph', displayName: toolName, description: toolName, readOnly, destructive: false,
  enabled: true, isAvailable: true, defaultSelected: true, accessClass: readOnly ? 'read' : 'write',
  requiresCredential: false, updatedAtUtc: '2026-11-01T00:00:00Z'
});
const refusal = (status: number, error: string, message: string) =>
  new HttpErrorResponse({ status, error: { error, message } });

describe('AdminAgentsComponent', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let fixture: ComponentFixture<AdminAgentsComponent>;
  let component: AdminAgentsComponent;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent ?? '';
  const button = (label: string) =>
    [...root().querySelectorAll('button')].find(b => b.textContent?.trim() === label) as HTMLButtonElement;
  const settle = async () => { await fixture.whenStable(); fixture.detectChanges(); };

  beforeEach(async () => {
    TestBed.resetTestingModule();
    vi.stubGlobal('confirm', vi.fn(() => true));
    api = {
      listAgents: vi.fn().mockReturnValue(of([codex])),
      createAgent: vi.fn().mockReturnValue(of({ ...codex, id: 8, name: 'claude', activeTokens: 0 })),
      updateAgent: vi.fn().mockReturnValue(of({ ...codex, status: 'disabled' })),
      deleteAgent: vi.fn().mockReturnValue(of(void 0)),
      listAgentTokens: vi.fn().mockReturnValue(of([token])),
      createAgentToken: vi.fn().mockReturnValue(of({ token, rawToken: 'cgpat_secret_value' })),
      updateAgentTokenTools: vi.fn().mockReturnValue(of({ ...token, toolNames: ['search_graph'] })),
      revokeAgentToken: vi.fn().mockReturnValue(of(void 0)),
      listUserMcpTools: vi.fn().mockReturnValue(of([tool('search_graph', true), tool('update_work', false)]))
    };
    await TestBed.configureTestingModule({
      imports: [AdminAgentsComponent],
      providers: [{ provide: ApiService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(AdminAgentsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  });

  it('lists agents with status and active token count', () => {
    expect(api['listAgents']).toHaveBeenCalledTimes(1);
    expect(text()).toContain('codex');
    expect(text()).toContain('active');
    expect(text()).toContain('1 active token');
    expect(text()).toContain('non-human identities');
  });

  it('creates an agent and reloads the list', async () => {
    component.newName = ' Claude ';
    await component.create();

    expect(api['createAgent']).toHaveBeenCalledWith('Claude');
    expect(api['listAgents']).toHaveBeenCalledTimes(2);
    expect(component.newName).toBe('');
    expect(component.success()).toBe('Created agent claude.');
  });

  it('shows the API refusal message for a bad name and for a conflict', async () => {
    api['createAgent'].mockReturnValue(throwError(() => refusal(400, 'invalid_argument', "'michael' is a person, not an agent.")));
    component.newName = 'michael';
    await component.create();
    fixture.detectChanges();
    expect(root().querySelector('.adm-banner.err')?.textContent).toContain("'michael' is a person, not an agent.");

    api['updateAgent'].mockReturnValue(throwError(() => refusal(409, 'conflict', 'codex has a live session.')));
    component.startRename(codex);
    component.renameValue = 'codex2';
    await component.saveRename(codex);
    expect(component.error()).toBe('codex has a live session.');
    expect(component.renamingId()).toBe(7);

    api['deleteAgent'].mockReturnValue(throwError(() => refusal(409, 'conflict', 'codex has a live session.')));
    await component.remove(codex);
    expect(component.error()).toBe('codex has a live session.');
  });

  it('renames, disables and enables an agent through PATCH', async () => {
    component.startRename(codex);
    component.renameValue = ' codex2 ';
    await component.saveRename(codex);
    expect(api['updateAgent']).toHaveBeenLastCalledWith(7, { name: 'codex2' });
    expect(component.renamingId()).toBeNull();

    await component.setDisabled(codex, true);
    expect(api['updateAgent']).toHaveBeenLastCalledWith(7, { disabled: true });
    await component.setDisabled({ ...codex, status: 'disabled' }, false);
    expect(api['updateAgent']).toHaveBeenLastCalledWith(7, { disabled: false });
  });

  it('deletes an agent only after confirmation', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    await component.remove(codex);
    expect(api['deleteAgent']).not.toHaveBeenCalled();

    vi.stubGlobal('confirm', vi.fn(() => true));
    await component.remove(codex);
    expect(api['deleteAgent']).toHaveBeenCalledWith(7);
    expect(component.success()).toBe('Deleted codex.');
  });

  it('lists the tokens of an agent when it is opened', async () => {
    button('Tokens').click();
    await settle();

    expect(api['listAgentTokens']).toHaveBeenCalledWith(7);
    expect(text()).toContain('build host');
    expect(text()).toContain('cgpat_abc...9xyz');
  });

  it('issues a token for all tools and shows the raw value once', async () => {
    button('Tokens').click();
    await settle();
    component.tokenName = 'build host';
    component.expiresInDays = 60;

    await component.issueToken(codex);
    fixture.detectChanges();

    expect(api['createAgentToken']).toHaveBeenCalledWith(7, 'build host', 60, undefined);
    expect(text()).toContain('cgpat_secret_value');
    expect(text()).toContain('the agent never sees it');

    button('Hide tokens').click();
    await settle();
    button('Tokens').click();
    await settle();
    expect(text()).not.toContain('cgpat_secret_value');
  });

  it('issues a token for the selected tools only', async () => {
    button('Tokens').click();
    await settle();
    component.tokenName = 'readonly';
    component.allTools.set(false);
    component.selectedToolNames.set(new Set(['search_graph']));

    await component.issueToken(codex);

    expect(api['createAgentToken']).toHaveBeenCalledWith(7, 'readonly', 90, ['search_graph']);
  });

  it('refuses an empty tool selection and shows a token refusal', async () => {
    button('Tokens').click();
    await settle();
    component.tokenName = 'readonly';
    component.allTools.set(false);
    component.selectedToolNames.set(new Set());
    await component.issueToken(codex);
    expect(api['createAgentToken']).not.toHaveBeenCalled();
    expect(component.tokenError()).toBe('Select at least one MCP tool.');

    component.allTools.set(true);
    api['createAgentToken'].mockReturnValue(throwError(() => refusal(409, 'conflict', 'The agent is disabled.')));
    await component.issueToken(codex);
    fixture.detectChanges();
    expect(text()).toContain('The agent is disabled.');
    expect(component.rawToken()).toBe('');
  });

  it('revokes a token with DELETE and reloads', async () => {
    button('Tokens').click();
    await settle();

    await component.revoke(codex, token);

    expect(api['revokeAgentToken']).toHaveBeenCalledWith(7, 12);
    expect(component.success()).toBe('Revoked build host.');
    expect(api['listAgentTokens']).toHaveBeenCalledTimes(2);
  });

  it('replaces the tool access of an active token', async () => {
    button('Tokens').click();
    await settle();
    component.startEditTools(token);
    component.editToolNames.set(new Set(['search_graph']));

    await component.saveEditTools(codex, token);

    expect(api['updateAgentTokenTools']).toHaveBeenCalledWith(7, 12, ['search_graph']);
    expect(component.editingTokenId()).toBeNull();
  });
});
