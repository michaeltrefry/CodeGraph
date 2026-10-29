import { DatePipe } from '@angular/common';
import { Component, OnChanges, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { KnowledgeApiService } from './knowledge-api.service';
import { IdempotencyKey, actorLabel, actorTitle, errorText, workError, words } from './knowledge-ui';
import {
  ADDRESSED_BY_KINDS,
  AddressedByKind,
  Challenge,
  ChallengeOutcome,
  EVIDENCE_KINDS,
  EvidenceKind,
  KnowledgeEntry,
  LESSON_ACTIVITIES,
  LessonActivity
} from './knowledge.models';

type LessonForm = 'edit' | 'retract' | 'reject' | 'addressed_by' | 'challenge' | 'relate' | null;

/**
 * The lesson actions an agent has, for a person (E8): promote (propose, approve, reject with a
 * reason, addressed_by), challenge (open with evidence, resolve: uphold, amend, retract) and record
 * (revise, retract, relate). Each is the agent's own REST call; nothing here is human-only (E6).
 */
@Component({
  selector: 'app-lesson-actions',
  imports: [DatePipe, FormsModule],
  templateUrl: './lesson-actions.component.html',
  styleUrl: './knowledge.scss',
  host: { class: 'kn-stack' }
})
export class LessonActionsComponent implements OnChanges {
  private readonly api = inject(KnowledgeApiService);

  readonly entry = input.required<KnowledgeEntry>();
  /** The lesson as it is after a write (or after a conflict reloaded it). */
  readonly changed = output<KnowledgeEntry>();

  readonly activities = LESSON_ACTIVITIES;
  readonly addressedKinds = ADDRESSED_BY_KINDS;
  readonly evidenceKinds = EVIDENCE_KINDS;
  readonly actorLabel = actorLabel;
  readonly actorTitle = actorTitle;
  readonly words = words;

  readonly form = signal<LessonForm>(null);
  readonly resolving = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  reason = '';
  editSummary = '';
  editActivity = new Set<LessonActivity>();
  addressedKind: AddressedByKind = 'skill';
  addressedName = '';
  addressedRef = '';
  addressedHarness = '';
  evidenceKind: EvidenceKind = 'counter_example';
  evidenceSummary = '';
  evidenceRef = '';
  relateTo = '';
  relation: 'same_as' | 'contradicts' = 'contradicts';
  outcome: ChallengeOutcome = 'uphold';
  amendSummary = '';
  readonly challengeKey = new IdempotencyKey();

  // The lesson the forms and the challenge key belong to.
  private shownId: string | null = null;

  /** Another lesson selected: its forms, banners and challenge key start fresh, so nothing typed for (or keyed to) the last one reaches it. */
  ngOnChanges(): void {
    const id = this.entry().id;
    if (this.shownId !== null && this.shownId !== id) this.reset();
    this.shownId = id;
  }

  private reset(): void {
    this.form.set(null);
    this.resolving.set(null);
    this.error.set(null);
    this.notice.set(null);
    this.reason = '';
    this.editSummary = '';
    this.editActivity = new Set();
    this.addressedKind = 'skill';
    this.addressedName = '';
    this.addressedRef = '';
    this.addressedHarness = '';
    this.evidenceKind = 'counter_example';
    this.evidenceSummary = '';
    this.evidenceRef = '';
    this.relateTo = '';
    this.relation = 'contradicts';
    this.outcome = 'uphold';
    this.amendSummary = '';
    this.challengeKey.reset();
  }

  open(form: LessonForm): void {
    this.form.set(this.form() === form ? null : form);
    this.resolving.set(null);
    this.error.set(null);
    this.reason = '';
    const entry = this.entry();
    if (form === 'edit') {
      this.editSummary = entry.summary;
      this.editActivity = new Set(entry.details?.applies_when?.activity ?? []);
    }
  }

  openResolve(challenge: Challenge, outcome: ChallengeOutcome): void {
    this.form.set(null);
    this.resolving.set(challenge.id);
    this.outcome = outcome;
    this.reason = '';
    this.amendSummary = this.entry().summary;
  }

  toggleActivity(activity: LessonActivity, on: boolean): void {
    const next = new Set(this.editActivity);
    if (on) next.add(activity);
    else next.delete(activity);
    this.editActivity = next;
  }

  openChallenges(): Challenge[] {
    return (this.entry().lesson?.challenges ?? []).filter(challenge => challenge.status === 'open');
  }

  async propose(): Promise<void> {
    await this.promote('propose', undefined, 'Proposed for rule status.');
  }

  async approve(): Promise<void> {
    await this.promote('approve', undefined, 'Approved: it is a rule now.');
  }

  async reject(): Promise<void> {
    if (!this.reason.trim()) return this.need('Give the reason it should not be a rule.');
    await this.promote('reject', this.reason.trim(), 'Rejected for rule status.');
  }

  async addressedBy(): Promise<void> {
    if (!this.addressedName.trim()) return this.need('Name the skill, instruction, hook, test or CI gate.');
    const needsHarness = this.addressedKind === 'instruction' || this.addressedKind === 'hook';
    if (needsHarness && !this.addressedHarness.trim()) return this.need('An instruction or hook names its harness.');
    const entry = this.entry();
    await this.run(() => firstValueFrom(this.api.promote(entry.id, {
      expected_version: entry.version,
      action: 'addressed_by',
      addressed_by: {
        kind: this.addressedKind,
        name: this.addressedName.trim(),
        ...(this.addressedRef.trim() ? { ref: this.addressedRef.trim() } : {}),
        ...(needsHarness ? { harness: this.addressedHarness.trim() } : {})
      }
    })).then(result => result.entry), 'Marked addressed locally.');
  }

  async retract(): Promise<void> {
    if (!this.reason.trim()) return this.need('Give a reason for retracting.');
    const entry = this.entry();
    await this.run(() => firstValueFrom(this.api.record({
      retract: { id: entry.id, expected_version: entry.version, reason: this.reason.trim() }
    })).then(result => result.entry), 'Retracted: it leaves briefings.');
  }

  async edit(): Promise<void> {
    const entry = this.entry();
    const summary = this.editSummary.trim();
    const activity = [...this.editActivity];
    const before = entry.details?.applies_when?.activity ?? [];
    const activityChanged = activity.length !== before.length || activity.some(value => !before.includes(value));
    if (!summary) return this.need('The lesson needs its correct behavior.');
    if (summary === entry.summary && !activityChanged) return this.need('Nothing changed.');
    await this.run(() => firstValueFrom(this.api.record({
      revise: {
        id: entry.id,
        expected_version: entry.version,
        ...(summary !== entry.summary ? { summary } : {}),
        ...(activityChanged ? { applies_when: { ...(entry.details?.applies_when ?? {}), activity } } : {})
      }
    })).then(result => result.entry), 'Saved a new revision.');
  }

  async openChallenge(): Promise<void> {
    if (!this.reason.trim() || !this.evidenceSummary.trim()) return this.need('A challenge needs a reason and evidence.');
    const entry = this.entry();
    const key = this.challengeKey;
    const ok = await this.run(() => firstValueFrom(this.api.challenge({
      idempotency_key: key.value,
      open: {
        id: entry.id,
        reason: this.reason.trim(),
        evidence: [{ kind: this.evidenceKind, summary: this.evidenceSummary.trim(), ...(this.evidenceRef.trim() ? { ref: this.evidenceRef.trim() } : {}) }]
      }
    })).then(result => result.rule), 'Challenge opened. The rule stays active and is served with it.');
    if (ok) {
      key.reset();
      this.evidenceSummary = '';
      this.evidenceRef = '';
    }
  }

  async resolve(): Promise<void> {
    const challengeId = this.resolving();
    if (!challengeId) return;
    if (!this.reason.trim()) return this.need('Give the reason for the outcome.');
    if (this.outcome === 'amend' && !this.amendSummary.trim()) return this.need('Write the amended rule.');
    const entry = this.entry();
    await this.run(() => firstValueFrom(this.api.challenge({
      resolve: {
        challenge_id: challengeId,
        expected_version: entry.version,
        outcome: this.outcome,
        reason: this.reason.trim(),
        ...(this.outcome === 'amend' ? { amend: { summary: this.amendSummary.trim() } } : {})
      }
    })).then(result => result.rule), `Challenge resolved: ${this.outcome}.`);
  }

  async relate(): Promise<void> {
    if (!this.relateTo.trim()) return this.need('Give the id of the other lesson.');
    const entry = this.entry();
    await this.run(() => firstValueFrom(this.api.record({
      relate: { id: entry.id, expected_version: entry.version, [this.relation]: this.relateTo.trim() }
    })).then(result => result.entry), this.relation === 'same_as' ? 'Merged into the other lesson.' : 'Linked as contradicting.');
  }

  private async promote(action: 'propose' | 'approve' | 'reject', reason: string | undefined, done: string): Promise<void> {
    const entry = this.entry();
    await this.run(() => firstValueFrom(this.api.promote(entry.id, {
      expected_version: entry.version,
      action,
      ...(reason ? { reason } : {})
    })).then(result => result.entry), done);
  }

  private need(message: string): void {
    this.error.set(message);
  }

  private async run(call: () => Promise<KnowledgeEntry>, done: string): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const entry = await call();
      this.form.set(null);
      this.resolving.set(null);
      this.reason = '';
      this.notice.set(done);
      // The lesson this write answered (a same_as merge answers the other one) keeps the banner.
      if (entry.id !== this.shownId) this.challengeKey.reset();
      this.shownId = entry.id;
      this.changed.emit(entry);
      return true;
    } catch (error) {
      this.error.set(errorText(error));
      if (workError(error).code === 'conflict') {
        try {
          this.changed.emit(await firstValueFrom(this.api.getEntry(this.entry().id)));
        } catch {
          // The banner already says why.
        }
      }
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
