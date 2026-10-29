import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { KnowledgeApiService } from './knowledge-api.service';
import { IdempotencyKey, KnowledgeProjectService, actorLabel, actorTitle, errorText, workError, words } from './knowledge-ui';
import { KNOWLEDGE_KINDS, KnowledgeConfidence, KnowledgeEntry, KnowledgeKind, KnowledgeStatus, RecallFilters } from './knowledge.models';

type StaleFilter = 'any' | 'stale' | 'fresh';
type StatusFilter = KnowledgeStatus | 'all';
type FormMode = 'retract' | 'revise' | 'supersede' | null;

const PAGE_SIZE = 10;
const MAX_CHAIN = 20;

/**
 * Knowledge (§8.6): browse and search a project's knowledge, its stale flags and supersession
 * chains. Writes are record verify / revise / retract / acknowledge and a superseding entry,
 * through the same route agents use.
 */
@Component({
  selector: 'app-knowledge-page',
  imports: [DatePipe, FormsModule],
  templateUrl: './knowledge-page.component.html',
  styleUrl: './knowledge.scss'
})
export class KnowledgePageComponent implements OnInit {
  private readonly api = inject(KnowledgeApiService);
  readonly project = inject(KnowledgeProjectService);

  readonly kinds = KNOWLEDGE_KINDS;
  readonly actorLabel = actorLabel;
  readonly actorTitle = actorTitle;
  readonly words = words;

  text = '';
  kind: KnowledgeKind | '' = '';
  status: StatusFilter = 'active';
  stale: StaleFilter = 'any';
  repository = '';

  readonly items = signal<KnowledgeEntry[]>([]);
  readonly nextCursor = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly selected = signal<KnowledgeEntry | null>(null);
  readonly chain = signal<KnowledgeEntry[]>([]);
  readonly mode = signal<FormMode>(null);
  readonly busy = signal(false);

  reason = '';
  reviseSummary = '';
  reviseBody = '';
  reviseConfidence: KnowledgeConfidence = 'medium';
  newSummary = '';
  newBody = '';
  newWhyFailed = '';
  readonly supersedeKey = new IdempotencyKey();

  private pageFilters: RecallFilters | null = null;
  private searchSequence = 0;
  private selectSequence = 0;

  async ngOnInit(): Promise<void> {
    if (await this.project.load()) await this.search();
  }

  async changeProject(key: string): Promise<void> {
    this.project.select(key);
    this.selected.set(null);
    this.chain.set([]);
    await this.search();
  }

  filters(cursor?: string): RecallFilters {
    const filters: RecallFilters = { project: this.project.selected()!, detail: 'full', limit: PAGE_SIZE };
    if (this.kind) filters.kind = [this.kind];
    filters.status = this.status === 'all' ? ['active', 'superseded', 'retracted'] : [this.status];
    if (this.stale !== 'any') filters.possibly_stale = this.stale === 'stale';
    if (this.text.trim()) filters.text = this.text.trim();
    if (this.repository.trim()) filters.repository = this.repository.trim();
    if (cursor) filters.cursor = cursor;
    return filters;
  }

  /**
   * The first page with the current filters, or (more) the next page with the filters that page was
   * read with: the cursor belongs to them, so a filter edited since does not leak into it. A
   * response that is no longer the latest request's is dropped.
   */
  async search(more = false, rethrow = false): Promise<void> {
    if (!this.project.selected()) return;
    const cursor = this.nextCursor();
    if (more && (!cursor || !this.pageFilters)) return;
    const filters = more ? { ...this.pageFilters!, cursor: cursor! } : this.filters();
    const sequence = ++this.searchSequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      const page = await firstValueFrom(this.api.recall<KnowledgeEntry>(filters));
      if (sequence !== this.searchSequence) return;
      if (!more) this.pageFilters = filters;
      this.items.set(more ? [...this.items(), ...page.items] : page.items);
      this.nextCursor.set(page.next_cursor ?? null);
    } catch (error) {
      if (rethrow) throw error;
      if (sequence === this.searchSequence) this.error.set(errorText(error));
    } finally {
      if (sequence === this.searchSequence) this.loading.set(false);
    }
  }

  async select(id: string): Promise<void> {
    const sequence = ++this.selectSequence;
    this.mode.set(null);
    this.notice.set(null);
    try {
      const entry = await firstValueFrom(this.api.getEntry(id));
      if (sequence !== this.selectSequence) return;
      this.show(entry);
      await this.refreshChain(entry, sequence);
    } catch (error) {
      if (sequence === this.selectSequence) this.error.set(errorText(error));
    }
  }

  // The chain of the entry, or none when it cannot be read (never the chain of the previous entry).
  private async refreshChain(entry: KnowledgeEntry, sequence: number): Promise<void> {
    this.chain.set([]);
    const chain = await this.loadChain(entry);
    if (sequence === this.selectSequence) this.chain.set(chain);
  }

  /** The supersession chain through the entry, oldest first: supersedes backwards, superseded_by forwards. */
  async loadChain(entry: KnowledgeEntry): Promise<KnowledgeEntry[]> {
    if (!entry.supersedes && !entry.superseded_by) return [];
    const older: KnowledgeEntry[] = [];
    const newer: KnowledgeEntry[] = [];
    const seen = new Set([entry.id]);
    let back = entry.supersedes;
    while (back && !seen.has(back) && older.length < MAX_CHAIN) {
      seen.add(back);
      const previous = await firstValueFrom(this.api.getEntry(back));
      older.unshift(previous);
      back = previous.supersedes;
    }
    let forward = entry.superseded_by;
    while (forward && !seen.has(forward) && newer.length < MAX_CHAIN) {
      seen.add(forward);
      const next = await firstValueFrom(this.api.getEntry(forward));
      newer.push(next);
      forward = next.superseded_by;
    }
    return [...older, entry, ...newer];
  }

  openForm(mode: FormMode): void {
    const entry = this.selected();
    this.mode.set(this.mode() === mode ? null : mode);
    this.reason = '';
    if (entry && mode === 'revise') {
      this.reviseSummary = entry.summary;
      this.reviseBody = entry.body ?? '';
      this.reviseConfidence = entry.confidence;
    }
    if (mode === 'supersede') {
      this.newSummary = '';
      this.newBody = '';
      this.newWhyFailed = '';
    }
  }

  async verify(): Promise<void> {
    const entry = this.selected();
    if (!entry) return;
    await this.write(() => firstValueFrom(this.api.record({ verify: { id: entry.id, expected_version: entry.version } })), 'Verified against the current index.');
  }

  async acknowledge(): Promise<void> {
    const entry = this.selected();
    if (!entry) return;
    await this.write(() => firstValueFrom(this.api.record({ acknowledge: { id: entry.id, expected_version: entry.version } })), 'Acknowledged: it leaves the review queue.');
  }

  async retract(): Promise<void> {
    const entry = this.selected();
    if (!entry || !this.reason.trim()) {
      this.error.set('Give a reason for retracting.');
      return;
    }
    await this.write(
      () => firstValueFrom(this.api.record({ retract: { id: entry.id, expected_version: entry.version, reason: this.reason.trim() } })),
      'Retracted.');
  }

  async revise(): Promise<void> {
    const entry = this.selected();
    if (!entry) return;
    const revise: { id: string; expected_version: number; summary?: string; body?: string; confidence?: KnowledgeConfidence; clear?: string[] } = {
      id: entry.id,
      expected_version: entry.version
    };
    if (this.reviseSummary.trim() && this.reviseSummary.trim() !== entry.summary) revise.summary = this.reviseSummary.trim();
    if (this.reviseBody !== (entry.body ?? '')) {
      if (this.reviseBody.trim()) revise.body = this.reviseBody;
      else revise.clear = ['body'];
    }
    if (this.reviseConfidence !== entry.confidence) revise.confidence = this.reviseConfidence;
    if (Object.keys(revise).length === 2) {
      this.error.set('Nothing changed.');
      return;
    }
    await this.write(() => firstValueFrom(this.api.record({ revise })), 'Saved a new revision.');
  }

  async supersede(): Promise<void> {
    const entry = this.selected();
    if (!entry || !this.newSummary.trim()) {
      this.error.set('Write the summary of the replacement.');
      return;
    }
    const key = this.supersedeKey;
    const ok = await this.write(() => firstValueFrom(this.api.record({
      project: entry.project_key,
      idempotency_key: key.value,
      entry: {
        kind: entry.kind,
        summary: this.newSummary.trim(),
        ...(this.newBody.trim() ? { body: this.newBody } : {}),
        ...(entry.kind === 'attempt' ? { why_failed: this.newWhyFailed.trim() } : {}),
        confidence: entry.confidence,
        supersedes: { id: entry.id, expected_version: entry.version }
      }
    })), 'Superseded by the new entry.');
    if (ok) key.reset();
  }

  private async write(call: () => Promise<{ entry: KnowledgeEntry }>, done: string): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    let result: { entry: KnowledgeEntry };
    try {
      result = await call();
    } catch (error) {
      this.error.set(errorText(error));
      const current = this.selected();
      if (workError(error).code === 'conflict' && current) await this.reload(current.id);
      this.busy.set(false);
      return false;
    }

    // The write is stored; a failure to read back after it is not a failed write.
    this.mode.set(null);
    this.notice.set(done);
    this.show(result.entry);
    try {
      await this.refreshChain(result.entry, ++this.selectSequence);
      await this.search(false, true);
    } catch (error) {
      this.error.set(`Saved, but reloading failed: ${errorText(error)}`);
    } finally {
      this.busy.set(false);
    }
    return true;
  }

  private async reload(id: string): Promise<void> {
    try {
      this.show(await firstValueFrom(this.api.getEntry(id)));
    } catch {
      // The banner already says why the write failed.
    }
  }

  // A different entry gets a fresh supersede key and empty forms: a key never replays another entry's write.
  private show(entry: KnowledgeEntry): void {
    if (this.selected()?.id !== entry.id) {
      this.supersedeKey.reset();
      this.mode.set(null);
      this.reason = '';
      this.newSummary = '';
      this.newBody = '';
      this.newWhyFailed = '';
    }
    this.selected.set(entry);
  }

  statusClass(status: KnowledgeStatus): string {
    return status === 'active' ? 'cg-chip-ok' : status === 'retracted' ? 'cg-chip-err' : '';
  }
}
