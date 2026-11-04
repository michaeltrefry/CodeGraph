import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { KnowledgeApiService } from './knowledge-api.service';
import { KnowledgeProjectService, actorLabel, actorTitle, errorText, workError, words } from './knowledge-ui';
import { Challenge, KnowledgeRow, ReviewItem, ReviewKind } from './knowledge.models';

/** An open form on one queue item: the action, and for a challenge which one; for a contradiction which side. */
export interface QueueForm {
  key: string;
  action: 'reject' | 'amend' | 'uphold' | 'retract';
  challengeId?: string;
  side?: 'entry' | 'other';
}

export const REVIEW_KIND_LABELS: Record<ReviewKind, string> = {
  challenge: 'Challenged rule',
  contradiction: 'Contradicting lessons',
  dream_proposal: 'Dreaming proposal'
};

const PAGE_SIZE = 20;

/**
 * Review queue (§8.1): only what an agent cannot resolve alone (CG-174): challenged rules,
 * contradicting lessons and the dreamer's proposals. No lesson waits here for approval and no
 * autonomous decision for acknowledgment. Informational (E6): nothing waits on it and a challenged
 * rule stays served. Actions: challenge resolve, record relate / retract / revise, dream_review.
 */
@Component({
  selector: 'app-review-queue-page',
  imports: [DatePipe, FormsModule, NgTemplateOutlet],
  templateUrl: './review-queue-page.component.html',
  styleUrl: './knowledge.scss'
})
export class ReviewQueuePageComponent implements OnInit {
  private readonly api = inject(KnowledgeApiService);
  readonly selection = inject(KnowledgeProjectService);

  /** Set by an embedding project page: the queue is that project's, without the page header or project select. */
  readonly project = input<string | undefined>();
  readonly embedded = computed(() => !!this.project());
  readonly current = computed(() => this.project() || this.selection.selected());

  readonly kindLabels = REVIEW_KIND_LABELS;
  readonly actorLabel = actorLabel;
  readonly actorTitle = actorTitle;
  readonly words = words;

  readonly items = signal<ReviewItem[]>([]);
  readonly nextCursor = signal<string | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly form = signal<QueueForm | null>(null);

  reason = '';
  amendSummary = '';

  private loadSequence = 0;

  constructor() {
    // The embedding page reuses this component when its project changes: start over for the new one.
    effect(() => {
      if (this.project()) untracked(() => void this.reopen());
    });
  }

  async ngOnInit(): Promise<void> {
    if (!this.embedded() && await this.selection.load()) await this.load();
  }

  async changeProject(key: string): Promise<void> {
    this.selection.select(key);
    await this.reopen();
  }

  private async reopen(): Promise<void> {
    this.items.set([]);
    this.nextCursor.set(null);
    this.form.set(null);
    this.error.set(null);
    this.notice.set(null);
    await this.load();
  }

  keyOf(item: ReviewItem): string {
    return `${item.kind}:${item.proposal?.id ?? item.entry.id}:${item.other?.id ?? ''}`;
  }

  async load(more = false): Promise<void> {
    const project = this.current();
    if (!project) return;
    const sequence = ++this.loadSequence;
    this.loading.set(true);
    try {
      const page = await firstValueFrom(this.api.reviewQueue(project, PAGE_SIZE, more ? this.nextCursor() ?? undefined : undefined));
      if (sequence !== this.loadSequence) return;
      this.items.set(more ? [...this.items(), ...page.items] : page.items);
      this.nextCursor.set(page.next_cursor ?? null);
    } catch (error) {
      if (sequence === this.loadSequence) this.error.set(errorText(error));
    } finally {
      if (sequence === this.loadSequence) this.loading.set(false);
    }
  }

  open(item: ReviewItem, action: QueueForm['action'], extra: Partial<QueueForm> = {}): void {
    const next: QueueForm = { key: this.keyOf(item), action, ...extra };
    const current = this.form();
    const same = current && current.key === next.key && current.action === next.action && current.challengeId === next.challengeId && current.side === next.side;
    this.form.set(same ? null : next);
    this.reason = '';
    const target = extra.side === 'other' && item.other ? item.other : item.entry;
    this.amendSummary = target.summary;
    this.error.set(null);
  }

  // ---- one-click actions ----------------------------------------------------------------------

  /** Accept a dreaming proposal. */
  async approve(item: ReviewItem): Promise<void> {
    if (item.kind === 'dream_proposal' && item.proposal) {
      await this.run(() => firstValueFrom(this.api.reviewDreamProposal(item.proposal!.id, this.current()!, item.proposal!.version, 'accept')),
        item.proposal.draft ? 'Draft accepted. Apply the draft outside CodeGraph.' : 'Proposal accepted.');
    }
  }

  /** Contradiction: the two lessons say the same; the first is folded into the second (record relate same_as). */
  async sameAs(item: ReviewItem): Promise<void> {
    const other = item.other;
    if (!other) return;
    await this.run(() => firstValueFrom(this.api.record({ relate: { id: item.entry.id, expected_version: item.entry.version, same_as: other.id } })),
      'Merged the first lesson into the second.');
  }

  /** Submit the open form of the item. */
  async submit(item: ReviewItem): Promise<void> {
    const form = this.form();
    if (!form || form.key !== this.keyOf(item)) return;
    const reason = this.reason.trim();
    const summary = this.amendSummary.trim();

    if (item.kind === 'dream_proposal' && item.proposal) {
      if (!reason) return this.need('Give a reason for rejecting this proposal.');
      await this.run(() => firstValueFrom(this.api.reviewDreamProposal(item.proposal!.id, this.current()!, item.proposal!.version, 'reject', reason)),
        'Rejected. The same action stays suppressed until new evidence arrives.');
      return;
    }

    if (form.challengeId) {
      if (!reason) return this.need('Give the reason for the outcome.');
      if (form.action === 'amend' && !summary) return this.need('Write the amended rule.');
      const outcome = form.action === 'amend' ? 'amend' : form.action === 'retract' ? 'retract' : 'uphold';
      await this.run(() => firstValueFrom(this.api.challenge({
        resolve: {
          challenge_id: form.challengeId!,
          expected_version: item.entry.version,
          outcome,
          reason,
          ...(outcome === 'amend' ? { amend: { summary } } : {})
        }
      })), `Challenge resolved: ${outcome}.`);
      return;
    }

    const target: KnowledgeRow = form.side === 'other' && item.other ? item.other : item.entry;
    if (form.action === 'reject' || form.action === 'retract') {
      if (!reason) return this.need('Give a reason for retracting.');
      await this.run(() => firstValueFrom(this.api.record({ retract: { id: target.id, expected_version: target.version, reason } })), 'Retracted.');
    } else if (form.action === 'amend') {
      if (!summary || summary === target.summary) return this.need('Change the text to amend it.');
      await this.amend(target, summary);
    }
  }

  // Revise one side of a contradiction.
  private async amend(target: KnowledgeRow, summary: string): Promise<void> {
    await this.run(() => firstValueFrom(this.api.record({ revise: { id: target.id, expected_version: target.version, summary } })), 'Amended.');
  }

  challengesOf(item: ReviewItem): Challenge[] {
    return item.challenges ?? [];
  }

  private need(message: string): void {
    this.error.set(message);
  }

  private async run(call: () => Promise<unknown>, done: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      await call();
      this.form.set(null);
      this.reason = '';
      this.notice.set(done);
    } catch (error) {
      this.error.set(errorText(error));
      if (workError(error).code !== 'conflict') return;
    } finally {
      this.busy.set(false);
    }
    // After a write, or a conflict: the queue as it is now.
    await this.load();
  }
}
