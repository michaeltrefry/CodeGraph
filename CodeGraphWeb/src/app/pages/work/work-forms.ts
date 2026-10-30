import { Component, OnInit, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { lines } from './work-base';
import {
  FeatureView,
  ItemView,
  ProjectView,
  WORK_ITEM_TYPES,
  WORK_PRIORITIES,
  WORK_STATUSES,
  WorkItemType,
  WorkPriority,
  WorkStatus
} from './work.models';

const csv = (text: string): string[] => text.split(',').map(part => part.trim()).filter(Boolean);
const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);

/** A modal around a form. Escape and the backdrop close it. */
@Component({
  selector: 'wrk-dialog',
  standalone: true,
  template: `
    <div class="wrk-dialog-back" (click)="closed.emit()" (keydown.escape)="closed.emit()" role="presentation">
      <div class="wrk-dialog" role="dialog" aria-modal="true" [attr.aria-label]="heading()" (click)="$event.stopPropagation()" tabindex="-1">
        <h2>{{ heading() }}</h2>
        <ng-content />
      </div>
    </div>
  `
})
export class DialogComponent {
  readonly heading = input.required<string>();
  readonly closed = output<void>();
}

/** Create or edit an item: title, type, priority, status, repository, labels, goal, acceptance criteria, inherits, touches, blockers. */
@Component({
  selector: 'wrk-item-form',
  standalone: true,
  imports: [FormsModule],
  template: `
    <form class="wrk-form" (ngSubmit)="submit()">
      <label>Title <input class="wrk-input" name="title" [(ngModel)]="title" required maxlength="240" /></label>
      <div class="wrk-form-row">
        <label>Type <select class="wrk-input" name="type" [(ngModel)]="type">@for (t of types; track t) { <option [value]="t">{{ t }}</option> }</select></label>
        <label>Priority <select class="wrk-input" name="priority" [(ngModel)]="priority">@for (p of priorities; track p) { <option [value]="p">{{ p }}</option> }</select></label>
        <label>Status <select class="wrk-input" name="status" [(ngModel)]="status">@for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }</select></label>
      </div>
      <div class="wrk-form-row">
        <label>Repository <input class="wrk-input" name="repository" [(ngModel)]="repository" /></label>
        <label>Feature <input class="wrk-input" name="feature" [(ngModel)]="feature" placeholder="SW-F2" /></label>
        <label>Labels (comma separated) <input class="wrk-input" name="labels" [(ngModel)]="labels" /></label>
        <label style="flex:0 0 90px">Order <input class="wrk-input" type="number" name="order" min="0" [(ngModel)]="order" /></label>
      </div>
      <label>Goal <textarea class="wrk-input" name="goal" rows="2" [(ngModel)]="goal"></textarea></label>
      <label>Acceptance criteria (one per line, at most 10) <textarea class="wrk-input" name="criteria" rows="3" [(ngModel)]="criteria"></textarea></label>
      <div class="wrk-form-row">
        <label>Inherits epic requirements (E1, E3) <input class="wrk-input" name="inherits" [(ngModel)]="inherits" /></label>
        <label>Blocked by (keys) <input class="wrk-input" name="blockedBy" [(ngModel)]="blockedBy" [disabled]="!!initial()" [title]="initial() ? 'Change blockers on the item page' : ''" /></label>
      </div>
      <label>Touches (paths and symbols, one per line) <textarea class="wrk-input" name="touches" rows="2" [(ngModel)]="touches"></textarea></label>
      <label>Description <textarea class="wrk-input" name="description" rows="3" [(ngModel)]="description"></textarea></label>
      <div class="wrk-dialog-actions">
        <button type="button" class="wrk-btn" (click)="cancelled.emit()">Cancel</button>
        <button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="!title.trim()">{{ initial() ? 'Save changes' : 'Create item' }}</button>
      </div>
    </form>
  `
})
export class ItemFormComponent implements OnInit {
  readonly initial = input<ItemView | undefined>();
  /** Preselected feature key when created from a feature page. */
  readonly featureKey = input('');
  readonly saved = output<Record<string, unknown>>();
  readonly cancelled = output<void>();
  readonly types = WORK_ITEM_TYPES;
  readonly priorities = WORK_PRIORITIES;
  readonly statuses = WORK_STATUSES;
  title = '';
  type: WorkItemType = 'story';
  priority: WorkPriority = 'medium';
  status: WorkStatus = 'backlog';
  repository = '';
  feature = '';
  labels = '';
  goal = '';
  criteria = '';
  inherits = '';
  blockedBy = '';
  order: number | null = null;
  touches = '';
  description = '';

  ngOnInit(): void {
    const item = this.initial();
    this.feature = this.featureKey();
    if (!item) return;
    this.title = item.title;
    this.type = item.type;
    this.priority = item.priority;
    this.status = item.status;
    this.repository = item.repository ?? '';
    this.feature = item.feature?.key ?? '';
    this.labels = item.labels.join(', ');
    this.goal = item.goal ?? '';
    this.criteria = (item.acceptance_criteria ?? []).map(criterion => criterion.text).join('\n');
    this.inherits = (item.inherits ?? []).join(', ');
    this.touches = (item.touches ?? []).join('\n');
    this.description = item.description ?? '';
    this.order = item.order;
  }

  submit(): void {
    const item = this.initial();
    const existing = item?.acceptance_criteria ?? [];
    const criteria = lines(this.criteria).map((text, index) => (existing[index] && existing[index].text === text ? { id: existing[index].id, text } : { ...(existing[index] ? { id: existing[index].id } : {}), text }));
    const all: Record<string, unknown> = {
      title: this.title.trim(),
      type: this.type,
      priority: this.priority,
      status: this.status,
      repository: this.repository.trim() || undefined,
      feature: this.feature.trim() || undefined,
      labels: csv(this.labels),
      goal: this.goal.trim() || undefined,
      acceptance_criteria: criteria,
      inherits: csv(this.inherits),
      touches: lines(this.touches),
      description: this.description.trim() || undefined,
      order: this.order === null || this.order === undefined || (this.order as unknown) === '' ? undefined : Number(this.order)
    };
    if (!item) {
      all['blocked_by'] = csv(this.blockedBy);
      this.saved.emit(Object.fromEntries(Object.entries(all).filter(([, value]) => value !== undefined && !(Array.isArray(value) && value.length === 0))));
      return;
    }

    const before: Record<string, unknown> = {
      title: item.title, type: item.type, priority: item.priority, status: item.status, repository: item.repository, feature: item.feature?.key,
      labels: item.labels, goal: item.goal, acceptance_criteria: existing.map(criterion => ({ id: criterion.id, text: criterion.text })),
      inherits: item.inherits ?? [], touches: item.touches ?? [], description: item.description, order: item.order
    };
    const changed: Record<string, unknown> = {};
    for (const [name, value] of Object.entries(all)) {
      if (same(value ?? undefined, before[name] ?? undefined)) continue;
      changed[name] = value;
    }
    // An emptied optional field is cleared rather than set to nothing.
    const clear: string[] = [];
    for (const name of ['repository', 'feature', 'goal', 'description']) {
      if (name in changed && changed[name] === undefined) {
        delete changed[name];
        clear.push(name);
      }
    }
    for (const name of ['labels', 'inherits', 'touches', 'acceptance_criteria']) {
      if (name in changed && Array.isArray(changed[name]) && (changed[name] as unknown[]).length === 0) {
        delete changed[name];
        clear.push(name);
      }
    }
    if (clear.length > 0) changed['clear'] = clear;
    this.saved.emit(changed);
  }
}

/** Create or edit a feature: title, description, goals, numbered requirements, acceptance tests. */
@Component({
  selector: 'wrk-feature-form',
  standalone: true,
  imports: [FormsModule],
  template: `
    <form class="wrk-form" (ngSubmit)="submit()">
      <label>Title <input class="wrk-input" name="title" [(ngModel)]="title" required maxlength="240" /></label>
      <label>Description <textarea class="wrk-input" name="description" rows="3" [(ngModel)]="description"></textarea></label>
      <label>Goals (one per line) <textarea class="wrk-input" name="goals" rows="2" [(ngModel)]="goals"></textarea></label>
      <label>Requirements (one per line, "E1: text") <textarea class="wrk-input" name="requirements" rows="4" [(ngModel)]="requirements"></textarea></label>
      <label>Acceptance tests (one per line) <textarea class="wrk-input" name="tests" rows="3" [(ngModel)]="tests"></textarea></label>
      @if (dropped().length > 0) {
        <div class="wrk-conflict">A requirement left out is dropped, and needs a decision. Dropped: {{ dropped().join(', ') }}.
          <label>Decision for the drop <input class="wrk-input" name="decision" [(ngModel)]="decision" /></label></div>
      }
      <div class="wrk-dialog-actions">
        <button type="button" class="wrk-btn" (click)="cancelled.emit()">Cancel</button>
        <button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="!title.trim() || (dropped().length > 0 && !decision.trim())">{{ initial() ? 'Save changes' : 'Create feature' }}</button>
      </div>
    </form>
  `
})
export class FeatureFormComponent implements OnInit {
  readonly initial = input<FeatureView | undefined>();
  readonly saved = output<Record<string, unknown>>();
  readonly cancelled = output<void>();
  title = '';
  description = '';
  goals = '';
  requirements = '';
  tests = '';
  decision = '';

  ngOnInit(): void {
    const feature = this.initial();
    if (!feature) return;
    this.title = feature.title;
    this.description = feature.description ?? '';
    this.goals = (feature.goals ?? []).join('\n');
    this.requirements = (feature.requirements ?? []).filter(requirement => requirement.state === 'active').map(requirement => `${requirement.id}: ${requirement.text}`).join('\n');
    this.tests = (feature.acceptance_tests ?? []).join('\n');
  }

  private parsedRequirements(): { id: string; text: string }[] {
    return lines(this.requirements).map(line => {
      const match = /^([A-Za-z]\w*)\s*[:.)-]\s*(.+)$/.exec(line);
      return match ? { id: match[1], text: match[2] } : { id: '', text: line };
    });
  }

  dropped(): string[] {
    const feature = this.initial();
    if (!feature) return [];
    const kept = new Set(this.parsedRequirements().map(requirement => requirement.id));
    return (feature.requirements ?? []).filter(requirement => requirement.state === 'active' && !kept.has(requirement.id)).map(requirement => requirement.id);
  }

  submit(): void {
    const feature = this.initial();
    // Requirements without an id are numbered after the highest in use.
    const parsed = this.parsedRequirements();
    let next = parsed.reduce((highest, requirement) => Math.max(highest, Number(/^E?(\d+)$/.exec(requirement.id)?.[1] ?? 0)), 0);
    const requirements = parsed.map(requirement => (requirement.id ? requirement : { id: `E${++next}`, text: requirement.text }));
    const body: Record<string, unknown> = {
      title: this.title.trim(),
      description: this.description.trim() || undefined,
      goals: lines(this.goals),
      requirements,
      acceptance_tests: lines(this.tests)
    };
    if (feature) {
      const dropped = this.dropped();
      if (dropped.length > 0) body['drop_requirements'] = dropped.map(id => ({ id, decision: this.decision.trim() }));
    }
    this.saved.emit(Object.fromEntries(Object.entries(body).filter(([, value]) => value !== undefined && !(Array.isArray(value) && value.length === 0 && !feature))));
  }
}

/** Create or edit a project: name, description, goals, member repositories. */
@Component({
  selector: 'wrk-project-form',
  standalone: true,
  imports: [FormsModule],
  template: `
    <form class="wrk-form" (ngSubmit)="submit()">
      @if (!initial()) {
        <label>Key (2 to 10 letters and digits, such as SW; cannot be changed) <input class="wrk-input" name="key" [(ngModel)]="key" required maxlength="10" /></label>
      }
      <label>Name <input class="wrk-input" name="name" [(ngModel)]="name" required maxlength="160" /></label>
      <label>Description <textarea class="wrk-input" name="description" rows="3" [(ngModel)]="description"></textarea></label>
      <label>Goals (one per line) <textarea class="wrk-input" name="goals" rows="3" [(ngModel)]="goals"></textarea></label>
      <label>Repositories (one per line: "name" owned, or "name dependency") <textarea class="wrk-input" name="repositories" rows="4" [(ngModel)]="repositories"></textarea></label>
      <div class="wrk-dialog-actions">
        <button type="button" class="wrk-btn" (click)="cancelled.emit()">Cancel</button>
        <button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="!name.trim() || (!initial() && !key.trim())">{{ initial() ? 'Save changes' : 'Create project' }}</button>
      </div>
    </form>
  `
})
export class ProjectFormComponent implements OnInit {
  readonly initial = input<ProjectView | undefined>();
  readonly saved = output<Record<string, unknown>>();
  readonly cancelled = output<void>();
  key = '';
  name = '';
  description = '';
  goals = '';
  repositories = '';

  ngOnInit(): void {
    const project = this.initial();
    if (!project) return;
    this.name = project.name;
    this.description = project.description ?? '';
    this.goals = project.goals.join('\n');
    this.repositories = project.repositories.map(member => `${member.repository} ${member.role}`).join('\n');
  }

  submit(): void {
    const repositories = lines(this.repositories).map(line => {
      const [repository, role] = line.split(/\s+/);
      return { repository, role: role === 'dependency' ? 'dependency' : 'owned' };
    });
    const body: Record<string, unknown> = {
      name: this.name.trim(),
      description: this.description.trim() || undefined,
      goals: lines(this.goals),
      repositories
    };
    if (!this.initial()) body['key'] = this.key.trim().toUpperCase();
    this.saved.emit(Object.fromEntries(Object.entries(body).filter(([, value]) => value !== undefined && !(Array.isArray(value) && value.length === 0 && !this.initial()))));
  }
}
