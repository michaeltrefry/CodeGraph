import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeProjectService } from '../knowledge/knowledge-ui';
import { AuthService } from '../../core/auth.service';
import { DreamingApiService } from './dreaming-api.service';
import { DreamingPageComponent } from './dreaming-page.component';
import { DreamEffects, DreamInstructionSet, DreamRun, DreamSettings } from './dreaming.models';

const settings: DreamSettings = { version: 'v1:1:13:1', enabled: true, default_provider: 'anthropic', default_model: 'test-model', max_turns: 12, max_tokens_per_run: 24000, minimum_interval_minutes: 60, schedule: '0 0 2 * * *', input_cost_per_million: 3, output_cost_per_million: 15 };
const instructions: DreamInstructionSet = {
  base: { slug: 'dreaming-base', content: 'Read evidence first', revision: 2, history: [{ revision: 1, content: 'Original base', edited_by: 'michael', created_at: '2026-10-29T12:00:00Z' }] },
  project: { slug: 'dreaming-project-one', content: '', revision: 0, history: [] }
};
const run: DreamRun = {
  id: 'run-one', project_id: 'project-one', trigger: 'run_now', initiator: { kind: 'user', name: 'michael' }, status: 'completed', started_at: '2026-10-30T12:00:00Z', ended_at: '2026-10-30T12:01:00Z',
  provider: 'anthropic', model: 'test-model', input_tokens: 1200, output_tokens: 300, estimated_cost: .0081, base_instruction_revision: 2, project_instruction_revision: 0,
  actions: [{ tool: 'revise', arguments: { id: 'rule-one' }, result: { changed: true }, evidence: { references: ['review-44'], prevalence: '4 sessions across 3 items' }, created_at: '2026-10-30T12:00:30Z' }], report: 'Narrowed applies-when based on recurring evidence.'
};
const effects: DreamEffects = { rules: [{ entry_id: 'rule-one', summary: 'Check exact provider', changed_at: '2026-10-30T12:00:30Z', before: { events: 5, while_served: 4, while_not_served: 0 }, after: { events: 2, while_served: 1, while_not_served: 1 } }] };

describe('DreamingPageComponent', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let project: { selected: ReturnType<typeof signal<string | null>>; projects: ReturnType<typeof signal<any[]>>; error: ReturnType<typeof signal<string | null>>; load: ReturnType<typeof vi.fn>; select: ReturnType<typeof vi.fn> };
  let params: Subject<ReturnType<typeof convertToParamMap>>;
  let auth: { enabled: ReturnType<typeof signal<boolean>>; currentUser: ReturnType<typeof signal<any>> };

  async function create() {
    await TestBed.configureTestingModule({ imports: [DreamingPageComponent], providers: [provideRouter([]),
      { provide: DreamingApiService, useValue: api }, { provide: KnowledgeProjectService, useValue: project },
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { paramMap: params } }
    ] }).compileComponents();
    const fixture = TestBed.createComponent(DreamingPageComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    params.next(convertToParamMap({ key: 'ONE' }));
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.resetTestingModule(); params = new Subject();
    auth = { enabled: signal(true), currentUser: signal({ isAdmin: true }) };
    project = { selected: signal<string | null>('ONE'), projects: signal([{ key: 'ONE', title: 'One' }, { key: 'TWO', title: 'Two' }]), error: signal<string | null>(null), load: vi.fn().mockResolvedValue('ONE'), select: vi.fn().mockImplementation(key => project.selected.set(key)) };
    api = {
      runs: vi.fn().mockReturnValue(of([run])), run: vi.fn().mockReturnValue(of(run)), runNow: vi.fn().mockReturnValue(of(run)), effects: vi.fn().mockReturnValue(of(effects)),
      settings: vi.fn().mockReturnValue(of(settings)), saveSettings: vi.fn().mockReturnValue(of(settings)), instructions: vi.fn().mockReturnValue(of(instructions)), saveInstructions: vi.fn().mockReturnValue(of(instructions.base)),
      revert: vi.fn().mockReturnValue(of({ run_id: run.id, entries: [{ journal_id: 1, entry_id: 'rule-one', action: 'revise', status: 'conflict', reason: 'Later edit by a session.' }] }))
    };
  });

  it('shows viewers shared settings as read-only while retaining run reports, run now and wiki editing', async () => {
    auth.currentUser.set({ isAdmin: false });
    const fixture = await create(); const page = fixture.componentInstance;
    page.tab.set('settings'); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('fieldset').disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('button[type=submit]').disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Administrator access');
    await page.saveSettings(); expect(api['saveSettings']).not.toHaveBeenCalled();
    await page.runNow(); expect(api['runNow']).toHaveBeenCalledWith('ONE'); expect(page.selected()?.id).toBe(run.id);
    page.tab.set('instructions'); fixture.detectChanges();
    await page.saveInstructions('base'); expect(api['saveInstructions']).toHaveBeenCalled();
    auth.currentUser.set({ isAdmin: true }); expect(page.canEditSettings()).toBe(true);
    auth.currentUser.set(null); auth.enabled.set(false); expect(page.canEditSettings()).toBe(false);
  });

  it.each([
    { enabled: false, isAdmin: false, mode: 'local development viewer' },
    { enabled: false, isAdmin: true, mode: 'local development admin' },
    { enabled: true, isAdmin: false, mode: 'JWT viewer' },
    { enabled: true, isAdmin: true, mode: 'JWT admin' }
  ])('uses the server admin permission for $mode settings controls', async ({ enabled, isAdmin }) => {
    auth.enabled.set(enabled); auth.currentUser.set({ isAdmin });
    const fixture = await create(); const page = fixture.componentInstance;
    page.tab.set('settings'); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('fieldset').disabled).toBe(!isAdmin);
    expect(fixture.nativeElement.querySelector('button[type=submit]').disabled).toBe(!isAdmin);
    await page.saveSettings();
    expect(api['saveSettings']).toHaveBeenCalledTimes(isAdmin ? 1 : 0);
  });

  it('lists run attribution, model, tokens, cost and action counts, and renders evidence and measured before/after', async () => {
    const fixture = await create();
    expect(fixture.nativeElement.textContent).toContain('michael');
    expect(fixture.nativeElement.textContent).toContain('1200 input');
    await fixture.componentInstance.select(run.id); fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    for (const value of ['test-model', '$0.0081', 'revise: 1', 'review-44', '4 sessions across 3 items', 'Base revision 2', 'Check exact provider', '4 recurred while served', '1 recurred while served']) expect(text).toContain(value);
  });

  it('run now opens completed or skipped reports and gives busy errors without losing the previous runs', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    api['runNow'].mockReturnValueOnce(of({ ...run, status: 'skipped', reason: 'No changed signals.' }));
    await page.runNow(); expect(api['runNow']).toHaveBeenCalledWith('ONE'); expect(page.notice()).toContain('skipped'); expect(page.selected()?.id).toBe(run.id);
    api['runNow'].mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'busy', message: 'A run is already active.' } })));
    await page.runNow(); expect(page.error()).toContain('already active'); expect(page.runs()).toHaveLength(1);
  });

  it('requires an explicit revert click then reports conflicts with the entry and reason', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    await page.select(run.id); await page.revert(); expect(api['revert']).not.toHaveBeenCalled();
    page.confirmRevert.set(true); await page.revert(); fixture.detectChanges();
    expect(api['revert']).toHaveBeenCalledWith('ONE', run.id);
    expect(fixture.nativeElement.textContent).toContain('conflict'); expect(fixture.nativeElement.textContent).toContain('Later edit by a session.');
  });

  it('edits every setting, with validation before calling the shared settings route', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    page.tab.set('settings'); fixture.detectChanges();
    for (const label of ['Provider', 'Model', 'Schedule', 'Minimum interval', 'Maximum turns', 'Maximum tokens', 'Input price', 'Output price', 'Enabled']) expect(fixture.nativeElement.textContent).toContain(label);
    page.settings = { ...settings, enabled: false, default_provider: 'openai', default_model: 'other', max_turns: 4, max_tokens_per_run: 8000, minimum_interval_minutes: 120, schedule: '0 0 3 * * *', input_cost_per_million: 2, output_cost_per_million: 8 };
    const edited = { ...page.settings }; await page.saveSettings(); expect(api['saveSettings']).toHaveBeenCalledWith('ONE', edited);
    page.settings!.max_turns = 1.5; await page.saveSettings(); expect(api['saveSettings']).toHaveBeenCalledTimes(1); expect(page.error()).toContain('whole-number');
  });

  it('preserves a stale client budget edit and refuses to overwrite another client disabling Dreaming', async () => {
    let persisted = { ...settings };
    api['settings'].mockImplementation(() => of({ ...persisted }));
    api['saveSettings'].mockImplementation((_project: string, submitted: DreamSettings) => {
      if (submitted.version !== persisted.version) return throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Settings or the schedule changed.' } }));
      persisted = { ...submitted, version: 'v1:2:13:2' };
      return of({ ...persisted });
    });
    const first = await create();
    const second = TestBed.createComponent(DreamingPageComponent);
    second.detectChanges(); await second.whenStable();
    params.next(convertToParamMap({ key: 'ONE' })); await second.whenStable();
    const clientA = first.componentInstance;
    const clientB = second.componentInstance;
    expect(clientA.settings?.version).toBe(settings.version);
    expect(clientB.settings?.version).toBe(settings.version);

    clientA.settings!.enabled = false;
    await clientA.saveSettings();
    expect(persisted.enabled).toBe(false);
    expect(clientA.settings?.version).toBe('v1:2:13:2');

    clientB.tab.set('settings');
    clientB.settings!.max_tokens_per_run = 48000;
    const draft = { ...clientB.settings! };
    const reads = api['settings'].mock.calls.length;
    await clientB.saveSettings(); second.detectChanges();
    expect(api['saveSettings']).toHaveBeenLastCalledWith('ONE', draft);
    expect(persisted.enabled).toBe(false);
    expect(persisted.max_tokens_per_run).toBe(settings.max_tokens_per_run);
    expect(clientB.settings).toEqual(draft);
    expect(api['settings']).toHaveBeenCalledTimes(reads);
    expect(clientB.settingsConflict()).toBe(true);
    expect(second.nativeElement.textContent).toContain('Your unsaved edits are preserved');
    expect((second.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true);
    await clientB.saveSettings();
    expect(api['saveSettings']).toHaveBeenCalledTimes(2);

    await clientB.reload();
    expect(clientB.settingsConflict()).toBe(false);
    expect(clientB.settings?.version).toBe('v1:2:13:2');
    expect(clientB.settings?.enabled).toBe(false);
  });

  it('edits base and optional project wiki instructions with their versions, showing full revision history', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    page.tab.set('instructions'); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Original base'); expect(fixture.nativeElement.textContent).toContain('michael');
    page.baseContent = 'New base'; await page.saveInstructions('base'); expect(api['saveInstructions']).toHaveBeenLastCalledWith('ONE', 'base', 'New base', 2);
    page.projectContent = ''; await page.saveInstructions('project'); expect(api['saveInstructions']).toHaveBeenLastCalledWith('ONE', 'project', '', 0);
    api['saveInstructions'].mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Changed.' } })));
    await page.saveInstructions('base'); expect(page.error()).toContain('reloaded'); expect(page.baseContent).toBe(instructions.base.content);
  });

  it('discards responses and details from the previous project', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    const old = new Subject<DreamRun>(); api['run'].mockReturnValueOnce(old);
    const selecting = page.select(run.id); await page.changeProject('TWO'); old.next(run); old.complete(); await selecting;
    expect(page.selected()).toBeNull(); expect(page.confirmRevert()).toBe(false); expect(page.revertResult()).toBeNull();
  });

  it('loads older runs by cursor without resetting unsaved settings or instruction text', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    page.runs.set(Array.from({ length: 30 }, (_, index) => ({ ...run, id: `run-${index}` })));
    page.hasMore.set(true); page.baseContent = 'Unsaved draft';
    api['runs'].mockReturnValueOnce(of([{ ...run, id: 'older-run' }]));
    await page.more();
    expect(api['runs']).toHaveBeenLastCalledWith('ONE', 30, 'run-29');
    expect(page.runs()).toHaveLength(31); expect(page.hasMore()).toBe(false);
    expect(page.baseContent).toBe('Unsaved draft'); expect(api['settings']).toHaveBeenCalledTimes(1);
  });

  it('keeps a report visible when effect analytics fail, with an explicit error', async () => {
    const fixture = await create(); const page = fixture.componentInstance;
    api['effects'].mockReturnValueOnce(throwError(() => new Error('Effect read unavailable')));
    await page.select(run.id); fixture.detectChanges();
    expect(page.selected()?.id).toBe(run.id); expect(fixture.nativeElement.textContent).toContain('Effect read unavailable');
  });
});
