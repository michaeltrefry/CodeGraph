import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeApiService } from './knowledge-api.service';
import { KnowledgePageComponent } from './knowledge-page.component';
import { AGENT, HUMAN, entry } from './knowledge-fixtures';
import { KnowledgeEntry } from './knowledge.models';

describe('KnowledgePageComponent', () => {
  const older = entry({ id: 'e-0', status: 'superseded', summary: 'Poll the inbox', superseded_by: 'e-1', author: HUMAN });
  const current = entry({ id: 'e-1', supersedes: 'e-0', possibly_stale: true, made_autonomously: true, stale_files: [{ repository: 'CG', path: 'src/a.cs' }] });
  let api: { [K in 'listProjects' | 'recall' | 'getEntry' | 'record']: ReturnType<typeof vi.fn> };

  async function create() {
    await TestBed.configureTestingModule({
      imports: [KnowledgePageComponent],
      providers: [{ provide: KnowledgeApiService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(KnowledgePageComponent);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) await new Promise(resolve => setTimeout(resolve));
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.resetTestingModule();
    const entries: Record<string, KnowledgeEntry> = { 'e-0': older, 'e-1': current };
    api = {
      listProjects: vi.fn().mockReturnValue(of([{ id: 'p', key: 'CG', kind: 'project', title: 'CodeGraph' }])),
      recall: vi.fn().mockReturnValue(of({ items: [current], next_cursor: 'more' })),
      getEntry: vi.fn((id: string) => of(entries[id])),
      record: vi.fn().mockReturnValue(of({ created: false, entry: { ...current, version: 4, possibly_stale: false } }))
    };
  });

  it('searches with the filters, whole entries, and renders stale flags and the actor', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;

    expect(api.recall).toHaveBeenCalledWith({ project: 'CG', detail: 'full', limit: 10, status: ['active'] });
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('possibly stale');
    expect(text).toContain('claude-code / opus @ dev-mac');

    page.text = 'SSE';
    page.kind = 'decision';
    page.stale = 'stale';
    page.status = 'all';
    page.repository = 'CG';
    await page.search();
    expect(api.recall).toHaveBeenLastCalledWith({
      project: 'CG', detail: 'full', limit: 10, kind: ['decision'], status: ['active', 'superseded', 'retracted'],
      possibly_stale: true, text: 'SSE', repository: 'CG'
    });

    await page.search(true);
    expect(api.recall.mock.calls.at(-1)![0].cursor).toBe('more');
  });

  it('shows the supersession chain of the selected entry, oldest first', async () => {
    const fixture = await create();
    await fixture.componentInstance.select('e-1');
    fixture.detectChanges();

    expect(fixture.componentInstance.chain().map(link => link.id)).toEqual(['e-0', 'e-1']);
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Supersession chain');
    expect(text).toContain('Poll the inbox');
    expect(text).toContain('michael');
  });

  it('verifies, acknowledges and retracts with the version it read', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');

    await page.verify();
    expect(api.record).toHaveBeenLastCalledWith({ verify: { id: 'e-1', expected_version: 3 } });

    page.selected.set(current);
    await page.acknowledge();
    expect(api.record).toHaveBeenLastCalledWith({ acknowledge: { id: 'e-1', expected_version: 3 } });

    page.selected.set(current);
    page.openForm('retract');
    await page.retract();
    expect(page.error()).toBe('Give a reason for retracting.');
    page.reason = 'Wrong transport';
    await page.retract();
    expect(api.record).toHaveBeenLastCalledWith({ retract: { id: 'e-1', expected_version: 3, reason: 'Wrong transport' } });
    expect(page.notice()).toBe('Retracted.');
  });

  it('offers unmerge for a merged lesson and sends its current version', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.selected.set(entry({ id: 'merged-1', kind: 'lesson', status: 'superseded',
      status_reason: 'Merged into e-1: the same lesson.', superseded_by: 'e-1', version: 7 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Unmerge');
    await page.unmerge();
    expect(api.record).toHaveBeenLastCalledWith({ unmerge: { id: 'merged-1', expected_version: 7 } });
    expect(page.notice()).toContain('Unmerged');
  });

  it('revises only the fields that changed', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    page.openForm('revise');
    await page.revise();
    expect(page.error()).toBe('Nothing changed.');

    page.reviseSummary = 'Use SSE for every stream';
    page.reviseConfidence = 'high';
    await page.revise();
    expect(api.record).toHaveBeenLastCalledWith({ revise: { id: 'e-1', expected_version: 3, summary: 'Use SSE for every stream', confidence: 'high' } });
  });

  it('keeps the supersede idempotency key across a failed retry and resets it after success', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    page.openForm('supersede');
    page.newSummary = 'Use WebSockets';

    api.record.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503, error: { detail: 'down' } })));
    await page.supersede();
    const firstKey = api.record.mock.calls[0][0].idempotency_key;
    expect(page.error()).toBe('down');

    await page.supersede();
    const retry = api.record.mock.calls[1][0];
    expect(retry.idempotency_key).toBe(firstKey);
    expect(retry.entry).toEqual({ kind: 'decision', summary: 'Use WebSockets', confidence: 'medium', supersedes: { id: 'e-1', expected_version: 3 } });
    expect(page.supersedeKey.value).not.toBe(firstKey);
  });

  it('reloads the entry when a write is refused conflict', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    api.getEntry.mockClear();
    api.record.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'conflict', message: 'Version 4 is current.' } })));

    await page.verify();

    expect(page.error()).toContain('reloaded');
    expect(api.getEntry).toHaveBeenCalledWith('e-1');
    expect(page.selected()?.author).toEqual(AGENT);
  });

  it('gives another entry a fresh supersede key and empty forms', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    page.openForm('supersede');
    page.newSummary = 'Typed for e-1';
    const key = page.supersedeKey.value;

    await page.select('e-0');

    expect(page.supersedeKey.value).not.toBe(key);
    expect(page.mode()).toBeNull();
    expect(page.newSummary).toBe('');
  });

  it('drops a selection answered after a newer one', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    const slow = new Subject<KnowledgeEntry>();
    const unchained = entry({ id: 'e-1' });
    api.getEntry.mockImplementation((id: string) => id === 'e-0' ? slow : of(unchained));

    const first = page.select('e-0');
    await page.select('e-1');
    slow.next(older);
    slow.complete();
    await first;

    expect(page.selected()?.id).toBe('e-1');
  });

  it('reads the next page with the filters of the first page', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    page.text = 'edited after the first page';
    await page.search(true);
    expect(api.recall).toHaveBeenLastCalledWith({ project: 'CG', detail: 'full', limit: 10, status: ['active'], cursor: 'more' });
  });

  it('clears the body through clear when it is emptied', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    api.getEntry.mockReturnValueOnce(of({ ...current, body: 'Old body' }));
    await page.select('e-1');
    page.openForm('revise');
    page.reviseBody = '';
    await page.revise();
    expect(api.record).toHaveBeenLastCalledWith({ revise: { id: 'e-1', expected_version: 3, clear: ['body'] } });
  });

  it('keeps a failed reload after a stored write apart from a failed write, and still resets the key', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    page.openForm('supersede');
    page.newSummary = 'Use WebSockets';
    const key = page.supersedeKey.value;
    api.recall.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 503, error: { detail: 'down' } })));

    await page.supersede();

    expect(page.notice()).toBe('Superseded by the new entry.');
    expect(page.error()).toBe('Saved, but reloading failed: down');
    expect(page.supersedeKey.value).not.toBe(key);
  });

  it('shows no chain of the previous entry when the new chain cannot be read', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    await page.select('e-1');
    expect(page.chain().length).toBe(2);
    api.getEntry.mockImplementation((id: string) => id === 'e-9'
      ? of(entry({ id: 'e-9', supersedes: 'gone' }))
      : throwError(() => new HttpErrorResponse({ status: 404, error: { code: 'not_found', message: 'No such entry.' } })));

    await page.select('e-9');

    expect(page.selected()?.id).toBe('e-9');
    expect(page.chain()).toEqual([]);
    expect(page.error()).toBe('No such entry.');
  });

  it('offers Acknowledge only on an autonomous decision not reviewed yet', async () => {
    const fixture = await create();
    const page = fixture.componentInstance;
    const buttons = () => [...fixture.nativeElement.querySelectorAll('button')].map((b: HTMLButtonElement) => b.textContent?.trim());
    await page.select('e-1');
    fixture.detectChanges();
    expect(buttons()).toContain('Acknowledge');

    api.getEntry.mockReturnValueOnce(of({ ...current, reviewed_at: '2026-10-29T10:00:00Z' }));
    await page.select('e-1');
    fixture.detectChanges();
    expect(buttons()).not.toContain('Acknowledge');
  });
});
