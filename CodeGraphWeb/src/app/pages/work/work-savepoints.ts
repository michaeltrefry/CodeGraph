import { Component, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { lines } from './work-base';

type SaveKind = 'decision' | 'attempt' | 'correction' | 'finding';

/**
 * Save points (§5.8): decisions, attempts, corrections and findings stored as knowledge in the same
 * transaction as an update_work. This builds the fields; the page sends them with its expected_version.
 */
@Component({
  selector: 'wrk-save-points',
  standalone: true,
  imports: [FormsModule],
  template: `
    <form class="wrk-form" (ngSubmit)="submit()">
      <label>Record a
        <select class="wrk-input" name="kind" [(ngModel)]="kind" aria-label="Save point kind">
          <option value="decision">decision</option><option value="attempt">failed attempt</option>
          <option value="correction">correction (lesson)</option><option value="finding">finding (fact, gotcha, constraint)</option>
        </select>
      </label>
      @switch (kind) {
        @case ('decision') {
          <label>Decision <input class="wrk-input" name="decision" [(ngModel)]="decision" /></label>
          <label>Why <input class="wrk-input" name="why" [(ngModel)]="why" /></label>
          <label>Alternatives considered (one per line) <textarea class="wrk-input" name="alternatives" rows="2" [(ngModel)]="alternatives"></textarea></label>
          <label style="display:flex;gap:6px;align-items:center"><input type="checkbox" name="autonomous" [(ngModel)]="autonomous" /> Made without asking a person</label>
        }
        @case ('attempt') {
          <label>What was tried <input class="wrk-input" name="tried" [(ngModel)]="tried" /></label>
          <label>Why it failed <input class="wrk-input" name="whyFailed" [(ngModel)]="whyFailed" /></label>
        }
        @case ('correction') {
          <label>What was wrong <input class="wrk-input" name="what" [(ngModel)]="what" /></label>
          <label>The correct behaviour <input class="wrk-input" name="correct" [(ngModel)]="correct" /></label>
          <label>Source
            <select class="wrk-input" name="source" [(ngModel)]="source">
              <option value="prompt">a person's prompt</option><option value="review">a review</option><option value="bot_review">a bot review</option>
              <option value="subagent">a subagent</option><option value="agent">another agent</option>
            </select>
          </label>
        }
        @case ('finding') {
          <div class="wrk-form-row">
            <label style="flex:0 0 140px">Kind <select class="wrk-input" name="fkind" [(ngModel)]="findingKind"><option value="fact">fact</option><option value="gotcha">gotcha</option><option value="constraint">constraint</option></select></label>
            <label>Summary <input class="wrk-input" name="summary" [(ngModel)]="summary" /></label>
          </div>
          <label>Detail <textarea class="wrk-input" name="fbody" rows="2" [(ngModel)]="detail"></textarea></label>
        }
      }
      <div class="wrk-row"><button type="submit" class="wrk-btn" [disabled]="!valid()">Record save point</button></div>
    </form>
  `
})
export class SavePointsComponent {
  /** The update_work fields to send, such as { decisions: [...] }. The page clears the form (clear) after the write succeeds. */
  readonly saved = output<Record<string, unknown>>();
  kind: SaveKind = 'decision';
  decision = '';
  why = '';
  alternatives = '';
  autonomous = false;
  tried = '';
  whyFailed = '';
  what = '';
  correct = '';
  source = 'prompt';
  findingKind = 'fact';
  summary = '';
  detail = '';

  valid(): boolean {
    switch (this.kind) {
      case 'decision': return !!this.decision.trim();
      case 'attempt': return !!this.tried.trim() && !!this.whyFailed.trim();
      case 'correction': return !!this.what.trim() && !!this.correct.trim();
      default: return !!this.summary.trim();
    }
  }

  submit(): void {
    if (!this.valid()) return;
    switch (this.kind) {
      case 'decision': {
        const entry: Record<string, unknown> = { decision: this.decision.trim() };
        if (this.why.trim()) entry['why'] = this.why.trim();
        if (lines(this.alternatives).length > 0) entry['alternatives'] = lines(this.alternatives);
        if (this.autonomous) entry['made_autonomously'] = true;
        this.saved.emit({ decisions: [entry] });
        break;
      }
      case 'attempt':
        this.saved.emit({ attempts: [{ tried: this.tried.trim(), why_failed: this.whyFailed.trim() }] });
        break;
      case 'correction':
        this.saved.emit({ corrections: [{ what: this.what.trim(), correct_behavior: this.correct.trim(), source: { kind: this.source } }] });
        break;
      default: {
        const entry: Record<string, unknown> = { kind: this.findingKind, summary: this.summary.trim() };
        if (this.detail.trim()) entry['body'] = this.detail.trim();
        this.saved.emit({ findings: [entry] });
      }
    }
  }

  /** Empties the fields; the page calls it once its update_work succeeded, so a refusal keeps what was typed. */
  clear(): void {
    this.decision = this.why = this.alternatives = this.tried = this.whyFailed = this.what = this.correct = this.summary = this.detail = '';
    this.autonomous = false;
  }
}
