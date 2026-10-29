import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
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
  proposed_rule: 'Proposed rule',
  contradiction: 'Contradicting lessons',
  autonomous_decision: 'Autonomous decision'
};

const PAGE_SIZE = 20;

/**
 * Review queue (§8.1): challenges, proposed rules, contradictions and decisions agents made on their
 * own. Informational (E6): nothing waits on it, a challenged rule stays served and a decision stands
 * until overturned. Every action is the agents' own call: promote approve / reject, challenge
 * resolve, record acknowledge / retract / revise.
 */
@Component({
  selector: 'app-review-queue-page',
  imports: [DatePipe, FormsModule, NgTemplateOutlet],
  templateUrl: './review-queue-page.component.html',
  styleUrl: './knowledge.scss'
})
export class ReviewQueuePageComponent implements OnInit {
  private readonly api = inject(KnowledgeApiService);
  readonly project = inject(KnowledgeProjectService);

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

  async ngOnInit(): Promise<void> {
    if (await this.project.load()) await this.load();
  }

  async changeProject(key: string): Promise<void> {
    this.project.select(key);
    await this.load();
  }

  keyOf(item: ReviewItem): string {
    return `${item.kind}:${item.entry.id}:${item.other?.id ?? ''}`;
  }

  async load(more = false): Promise<void> {
    const project = this.project.selected();
    if (!project) return;
    this.loading.set(true);
    try {
      const page = await firstValueFrom(this.api.reviewQueue(project, PAGE_SIZE, more ? this.nextCursor() ?? undefined : undefined));
      this.items.set(more ? [...this.items(), ...page.items] : page.items);
      this.nextCursor.set(page.next_cursor ?? null);
    } catch (error) {
      this.error.set(errorText(error));
    } finally {
      this.loading.set(false);
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

  /** Approve: a proposed rule becomes a rule; an autonomous decision is acknowledged (it stands). */
  async approve(item: ReviewItem): Promise<void> {
    if (item.kind === 'proposed_rule') {
      await this.run(() => firstValueFrom(this.api.promote(item.entry.id, { expected_version: item.entry.version, action: 'approve' })), 'Approved: it is a rule now.');
    } else if (item.kind === 'autonomous_decision') {
      await this.run(() => firstValueFrom(this.api.record({ acknowledge: { id: item.entry.id, expected_version: item.entry.version } })), 'Acknowledged: the decision stands.');
    }
  }

  /** Submit the open form of the item. */
  async submit(item: ReviewItem): Promise<void> {
    const form = this.form();
    if (!form || form.key !== this.keyOf(item)) return;
    const reason = this.reason.trim();
    const summary = this.amendSummary.trim();

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
    if (form.action === 'reject' && item.kind === 'proposed_rule') {
      if (!reason) return this.need('Give the reason it should not be a rule.');
      await this.run(() => firstValueFrom(this.api.promote(target.id, { expected_version: target.version, action: 'reject', reason })), 'Rejected: it is not proposed again.');
    } else if (form.action === 'reject' || form.action === 'retract') {
      if (!reason) return this.need('Give a reason for retracting.');
      await this.run(() => firstValueFrom(this.api.record({ retract: { id: target.id, expected_version: target.version, reason } })), 'Retracted.');
    } else if (form.action === 'amend') {
      if (!summary || summary === target.summary) return this.need('Change the text to amend it.');
      await this.run(async () => {
        const revised = await firstValueFrom(this.api.record({ revise: { id: target.id, expected_version: target.version, summary } }));
        // A person amending an autonomous decision has reviewed it: it is acknowledged at the new version.
        if (item.kind === 'autonomous_decision') {
          await firstValueFrom(this.api.record({ acknowledge: { id: target.id, expected_version: revised.entry.version } }));
        }
        return revised;
      }, item.kind === 'autonomous_decision' ? 'Amended and acknowledged.' : 'Amended.');
    }
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
