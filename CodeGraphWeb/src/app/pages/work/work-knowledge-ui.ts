import { Component, OnInit, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ErrorComponent, KeyHolder, WorkPageBase } from './work-base';
import { ActorComponent, AgoPipe } from './work-shared';
import { KnowledgeEntry, KnowledgeImportResult } from './work.models';

const KINDS = ['goal', 'decision', 'constraint', 'fact', 'gotcha', 'attempt', 'architecture', 'lesson'] as const;

/**
 * The project's knowledge (S5): recall (browse and search, with who wrote each entry), record, revise,
 * verify and retract, and import_knowledge (a dry run, then the confirmed store of the accepted candidates).
 * The richer browsing views (lessons, review queue) are S11's.
 */
@Component({
  selector: 'wrk-knowledge',
  standalone: true,
  imports: [FormsModule, ErrorComponent, ActorComponent, AgoPipe],
  template: `
    <wrk-error [error]="error()" (reload)="search()" />
    @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }
    <form class="wrk-filters" (ngSubmit)="search()">
      <select class="wrk-input" name="kind" aria-label="Kind" [(ngModel)]="kind"><option value="">all kinds</option>@for (k of kinds; track k) { <option [value]="k">{{ k }}</option> }</select>
      <input class="wrk-input" name="text" aria-label="Search words" placeholder="Search words" [(ngModel)]="text" />
      <label><input type="checkbox" name="stale" [(ngModel)]="staleOnly" /> possibly stale</label>
      <label><input type="checkbox" name="inactive" [(ngModel)]="includeInactive" /> superseded and retracted</label>
      <input class="wrk-input" name="reason" aria-label="Reason for retracting" placeholder="Reason, to retract an entry" [(ngModel)]="reason" />
      <button type="submit" class="wrk-btn">Search</button>
    </form>
    @if (rows().length === 0) { <div class="wrk-empty">No knowledge matches.</div> }
    <ul class="wrk-list" data-testid="knowledge">
      @for (row of rows(); track row.id) {
        <li>
          <div class="wrk-row">
            <span class="wrk-chip">{{ row.kind }}</span>
            @if (row.possibly_stale) { <span class="wrk-chip wrk-chip-open">possibly stale</span> }
            @if (row.status && row.status !== 'active') { <span class="wrk-chip">{{ row.status }}</span> }
            @if (row.made_autonomously) { <span class="wrk-chip">decided autonomously</span> }
            <span class="wrk-sub">{{ row.confidence }} · v{{ row.version }}@if (row.revision) { · revision {{ row.revision }} }</span>
            @if (row.author) { <wrk-actor [actor]="row.author" /> }
            @if (row.updated_at) { <span class="wrk-sub">{{ row.updated_at | ago }}</span> }
            <span class="wrk-grow"></span>
            @if (!row.status || row.status === 'active') {
              <button type="button" class="wrk-btn wrk-btn-quiet" [disabled]="busy()" (click)="startRevise(row)">Revise</button>
              <button type="button" class="wrk-btn wrk-btn-quiet" [disabled]="busy()" (click)="verify(row)">Verify</button>
              <button type="button" class="wrk-btn wrk-btn-quiet wrk-btn-danger" [disabled]="busy() || !reason.trim()" (click)="retract(row)">Retract</button>
            }
          </div>
          <div>{{ row.summary }}</div>
          @if (row.body) { <div class="wrk-pre wrk-sub">{{ row.body }}</div> }
          @if (revising()?.id === row.id) {
            <form class="wrk-form" (ngSubmit)="revise(row)">
              <label>Summary <input class="wrk-input" name="rsum" [(ngModel)]="reviseSummary" maxlength="500" /></label>
              <label>Body <textarea class="wrk-input" name="rbod" rows="3" [(ngModel)]="reviseBody"></textarea></label>
              <div class="wrk-row"><button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !reviseSummary.trim()">Save revision</button>
                <button type="button" class="wrk-btn wrk-btn-quiet" (click)="revising.set(null)">Cancel</button></div>
            </form>
          }
        </li>
      }
    </ul>
    @if (more()) { <div class="wrk-row"><button type="button" class="wrk-btn" [disabled]="busy()" (click)="loadMore()">More</button></div> }

    <details class="wrk-acquire">
      <summary>Record knowledge</summary>
      <form class="wrk-form" (ngSubmit)="record()">
        <div class="wrk-form-row">
          <label>Kind <select class="wrk-input" name="rkind" [(ngModel)]="newKind">@for (k of kinds; track k) { <option [value]="k">{{ k }}</option> }</select></label>
          <label>Confidence <select class="wrk-input" name="rconf" [(ngModel)]="confidence"><option>high</option><option>medium</option><option>low</option></select></label>
        </div>
        <label>Summary <input class="wrk-input" name="rsummary" [(ngModel)]="summary" maxlength="500" /></label>
        <label>Body <textarea class="wrk-input" name="rbody" rows="3" [(ngModel)]="body"></textarea></label>
        @if (newKind === 'decision') { <label>Why <input class="wrk-input" name="rwhy" [(ngModel)]="why" /></label> }
        @if (newKind === 'attempt') { <label>Why it failed <input class="wrk-input" name="rfail" [(ngModel)]="why" /></label> }
        <div class="wrk-row"><button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !summary.trim()">Record</button></div>
      </form>
    </details>

    <details class="wrk-acquire">
      <summary>Import knowledge (from harness memory files)</summary>
      <p class="wrk-sub">Paste the candidates as JSON: <code>[{{ '{' }}"entry": {{ '{' }}"kind", "summary"{{ '}' }}, "source_file", "verification": "verified|stale|unverifiable|personal"{{ '}' }}]</code>. The dry run stores nothing; then store the ones that would store.</p>
      <textarea class="wrk-input" rows="8" aria-label="Import candidates JSON" spellcheck="false" [(ngModel)]="importJson"></textarea>
      @if (importError()) { <div class="wrk-error">{{ importError() }}</div> }
      <div class="wrk-row" style="margin-top:6px"><button type="button" class="wrk-btn" [disabled]="busy() || !importJson.trim()" (click)="previewImport()">Dry run</button></div>
      @if (importPreview(); as preview) {
        <ul class="wrk-list" data-testid="import-preview">
          @for (c of preview.candidates; track c.index) {
            <li><div class="wrk-row">
              @if (c.outcome === 'would_store') { <input type="checkbox" [attr.aria-label]="'Accept candidate ' + c.index" [checked]="accepted().has(c.index)" (change)="toggle(c.index)" /> }
              <strong>#{{ c.index }}</strong><span class="wrk-chip" [attr.data-status]="c.outcome === 'would_store' || c.outcome === 'stored' ? 'complete' : 'closed'">{{ c.outcome.replace('_', ' ') }}</span>
              <span class="wrk-sub">{{ c.message }}</span></div></li>
          }
        </ul>
        <div class="wrk-row"><button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || accepted().size === 0 || preview.stored" (click)="confirmImport(preview)">Store {{ accepted().size }} accepted</button></div>
      }
    </details>
  `,
  styles: [`.wrk-acquire { margin-top: 10px; } .wrk-acquire summary { cursor: pointer; color: var(--wrk-link); }`]
})
export class KnowledgeComponent extends WorkPageBase implements OnInit {
  readonly project = input.required<string>();
  readonly rows = signal<KnowledgeEntry[]>([]);
  readonly more = signal('');
  readonly revising = signal<KnowledgeEntry | null>(null);
  readonly importPreview = signal<KnowledgeImportResult | null>(null);
  readonly accepted = signal<Set<number>>(new Set());
  readonly importError = signal('');
  readonly kinds = KINDS;
  private readonly recordKey = new KeyHolder('ui-record');
  private readonly importKey = new KeyHolder('ui-import');
  kind = '';
  text = '';
  staleOnly = false;
  includeInactive = false;
  newKind: (typeof KINDS)[number] = 'decision';
  confidence = 'medium';
  summary = '';
  body = '';
  why = '';
  reason = '';
  reviseSummary = '';
  reviseBody = '';
  importJson = '';

  constructor() {
    super();
  }

  ngOnInit(): void {
    void this.search();
  }

  protected reload(): Promise<void> {
    return this.search();
  }

  private query(cursor?: string): Record<string, unknown> {
    // detail full: whole entries with their author, ten to a page.
    const request: Record<string, unknown> = { project: this.project(), detail: 'full', limit: 10 };
    if (this.kind) request['kind'] = [this.kind];
    if (this.text.trim()) request['text'] = this.text.trim();
    if (this.staleOnly) request['possibly_stale'] = true;
    if (this.includeInactive) request['status'] = ['active', 'superseded', 'retracted'];
    if (cursor) request['cursor'] = cursor;
    return request;
  }

  async search(): Promise<void> {
    const result = await this.run(() => this.api.recall(this.query()));
    if (result) {
      this.rows.set(result.items);
      this.more.set(result.next_cursor ?? '');
    }
  }

  async loadMore(): Promise<void> {
    const result = await this.run(() => this.api.recall(this.query(this.more())));
    if (result) {
      this.rows.update(rows => [...rows, ...result.items]);
      this.more.set(result.next_cursor ?? '');
    }
  }

  async record(): Promise<void> {
    const entry: Record<string, unknown> = { kind: this.newKind, summary: this.summary.trim(), confidence: this.confidence };
    if (this.body.trim()) entry['body'] = this.body.trim();
    if (this.why.trim()) entry[this.newKind === 'attempt' ? 'why_failed' : 'why'] = this.why.trim();
    const done = await this.run(() => this.api.record({ project: this.project(), entry, idempotency_key: this.recordKey.key }), 'Recorded.');
    if (done) {
      this.recordKey.reset();
      this.summary = this.body = this.why = '';
      await this.search();
    }
  }

  startRevise(row: KnowledgeEntry): void {
    this.reviseSummary = row.summary;
    this.reviseBody = row.body ?? '';
    this.revising.set(row);
  }

  /** record with revise: a new revision of an active entry, guarded by the version just read. */
  async revise(row: KnowledgeEntry): Promise<void> {
    const revise: Record<string, unknown> = { id: row.id, expected_version: row.version };
    if (this.reviseSummary.trim() !== row.summary) revise['summary'] = this.reviseSummary.trim();
    if (this.reviseBody !== (row.body ?? '')) revise['body'] = this.reviseBody;
    if (await this.run(() => this.api.record({ project: this.project(), revise }), 'Revision saved.')) {
      this.revising.set(null);
      await this.search();
    }
  }

  async verify(row: KnowledgeEntry): Promise<void> {
    if (await this.run(() => this.api.record({ project: this.project(), verify: { id: row.id, expected_version: row.version } }), 'Verified.')) await this.search();
  }

  async retract(row: KnowledgeEntry): Promise<void> {
    if (await this.run(() => this.api.record({ project: this.project(), retract: { id: row.id, expected_version: row.version, reason: this.reason.trim() } }), 'Retracted.')) await this.search();
  }

  // ---- import_knowledge: a dry run, then confirm with the accepted indexes and the preview token ------

  toggle(index: number): void {
    this.accepted.update(current => {
      const next = new Set(current);
      if (!next.delete(index)) next.add(index);
      return next;
    });
  }

  private candidates(): unknown[] | null {
    this.importError.set('');
    try {
      const parsed = JSON.parse(this.importJson);
      if (!Array.isArray(parsed)) throw new Error('expected a JSON array of candidates');
      return parsed;
    } catch (error) {
      this.importError.set(`That is not a JSON array of candidates: ${error instanceof Error ? error.message : error}`);
      return null;
    }
  }

  async previewImport(): Promise<void> {
    const candidates = this.candidates();
    if (!candidates) return;
    const result = await this.run(() => this.api.importKnowledge({ project: this.project(), candidates }));
    if (result) {
      this.importPreview.set(result);
      this.accepted.set(new Set(result.candidates.filter(candidate => candidate.outcome === 'would_store').map(candidate => candidate.index)));
    }
  }

  async confirmImport(preview: KnowledgeImportResult): Promise<void> {
    const candidates = this.candidates();
    if (!candidates) return;
    const result = await this.run(() => this.api.importKnowledge({
      project: this.project(),
      candidates,
      confirm: true,
      accept: [...this.accepted()].sort((a, b) => a - b),
      preview_token: preview.preview_token,
      idempotency_key: this.importKey.key
    }), 'Imported.');
    if (result) {
      this.importKey.reset();
      this.importPreview.set(result);
      this.importJson = '';
      await this.search();
    }
  }
}
