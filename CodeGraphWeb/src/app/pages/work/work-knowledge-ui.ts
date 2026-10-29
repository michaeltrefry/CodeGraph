import { Component, OnInit, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ErrorComponent, WorkPageBase, idempotencyKey } from './work-base';
import { KnowledgeRow } from './work.models';

const KINDS = ['goal', 'decision', 'constraint', 'fact', 'gotcha', 'attempt', 'architecture', 'lesson'] as const;

/** The project's knowledge: recall (browse and search) and record, verify and retract (record). */
@Component({
  selector: 'wrk-knowledge',
  standalone: true,
  imports: [FormsModule, ErrorComponent],
  template: `
    <wrk-error [error]="error()" (reload)="search()" />
    <form class="wrk-filters" (ngSubmit)="search()">
      <select class="wrk-input" name="kind" aria-label="Kind" [(ngModel)]="kind"><option value="">all kinds</option>@for (k of kinds; track k) { <option [value]="k">{{ k }}</option> }</select>
      <input class="wrk-input" name="text" aria-label="Search words" placeholder="Search words" [(ngModel)]="text" />
      <label><input type="checkbox" name="stale" [(ngModel)]="staleOnly" /> possibly stale</label>
      <label><input type="checkbox" name="inactive" [(ngModel)]="includeInactive" /> superseded and retracted</label>
      <input class="wrk-input" name="reason" aria-label="Reason for retracting" placeholder="Reason, to retract an entry" [(ngModel)]="reason" />
      <button type="submit" class="wrk-btn">Search</button>
    </form>
    @if (rows().length === 0) { <div class="wrk-empty">No knowledge matches.</div> }
    <ul class="wrk-list">
      @for (row of rows(); track row.id) {
        <li>
          <div class="wrk-row">
            <span class="wrk-chip">{{ row.kind }}</span>
            @if (row.possibly_stale) { <span class="wrk-chip wrk-chip-open">possibly stale</span> }
            @if (row.status && row.status !== 'active') { <span class="wrk-chip">{{ row.status }}</span> }
            @if (row.made_autonomously) { <span class="wrk-chip">decided autonomously</span> }
            <span class="wrk-sub">{{ row.confidence }} · v{{ row.version }}</span>
            <span class="wrk-grow"></span>
            @if (!row.status || row.status === 'active') {
              <button type="button" class="wrk-btn wrk-btn-quiet" [disabled]="busy()" (click)="verify(row)">Verify</button>
              <button type="button" class="wrk-btn wrk-btn-quiet wrk-btn-danger" [disabled]="busy() || !reason.trim()" (click)="retract(row)">Retract</button>
            }
          </div>
          <div>{{ row.summary }}</div>
        </li>
      }
    </ul>
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
        <div class="wrk-row"><button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !summary.trim()">Record</button>
          @if (notice()) { <span class="wrk-sub">{{ notice() }}</span> }</div>
      </form>
    </details>
  `,
  styles: [`.wrk-acquire { margin-top: 10px; } .wrk-acquire summary { cursor: pointer; color: var(--wrk-link); }`]
})
export class KnowledgeComponent extends WorkPageBase implements OnInit {
  readonly project = input.required<string>();
  readonly rows = signal<KnowledgeRow[]>([]);
  readonly kinds = KINDS;
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

  constructor() {
    super();
  }

  ngOnInit(): void {
    void this.search();
  }

  protected reload(): Promise<void> {
    return this.search();
  }

  async search(): Promise<void> {
    const request: Record<string, unknown> = { project: this.project(), limit: 50 };
    if (this.kind) request['kind'] = [this.kind];
    if (this.text.trim()) request['text'] = this.text.trim();
    if (this.staleOnly) request['possibly_stale'] = true;
    if (this.includeInactive) request['status'] = ['active', 'superseded', 'retracted'];
    const result = await this.run(() => this.api.recall(request));
    if (result) this.rows.set(result.items);
  }

  async record(): Promise<void> {
    const entry: Record<string, unknown> = { kind: this.newKind, summary: this.summary.trim(), confidence: this.confidence };
    if (this.body.trim()) entry['body'] = this.body.trim();
    if (this.why.trim()) entry[this.newKind === 'attempt' ? 'why_failed' : 'why'] = this.why.trim();
    const done = await this.run(() => this.api.record({ project: this.project(), entry, idempotency_key: idempotencyKey('ui-record') }), 'Recorded.');
    if (done) {
      this.summary = this.body = this.why = '';
      await this.search();
    }
  }

  async verify(row: KnowledgeRow): Promise<void> {
    if (await this.run(() => this.api.record({ project: this.project(), verify: { id: row.id, expected_version: row.version } }), 'Verified.')) await this.search();
  }

  async retract(row: KnowledgeRow): Promise<void> {
    if (await this.run(() => this.api.record({ project: this.project(), retract: { id: row.id, expected_version: row.version, reason: this.reason.trim() } }), 'Retracted.')) await this.search();
  }
}
