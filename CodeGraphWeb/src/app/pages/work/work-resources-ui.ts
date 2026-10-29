import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ErrorComponent, WorkPageBase, idempotencyKey } from './work-base';
import { ActorComponent, AgoPipe } from './work-shared';
import { HoldView, ResourceView, WORK_PRIORITIES, WorkPriority } from './work.models';

/** Resources of a project with who holds each slot, who waits, and the acquire / renew / release actions. */
@Component({
  selector: 'wrk-resources',
  standalone: true,
  imports: [FormsModule, RouterLink, ActorComponent, AgoPipe, ErrorComponent],
  template: `
    <wrk-error [error]="error()" [reloadable]="false" />
    @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }
    @if (resources().length === 0) { <div class="wrk-empty">No resource has been taken in this project yet.</div> }
    @for (resource of resources(); track resource.id) {
      <section class="wrk-resource">
        <div class="wrk-row">
          <strong>{{ resource.name }}</strong>
          <span class="wrk-chip">{{ resource.used }} / {{ resource.capacity }} in use</span>
          @for (label of resource.labels; track label) { <span class="wrk-chip">{{ label }}</span> }
        </div>
        <ul class="wrk-list">
          @for (hold of resource.holders; track hold.id) {
            <li>
              <div class="wrk-row">
                <wrk-actor [actor]="hold.holder" />
                <span class="wrk-chip" data-status="in_progress">holds</span>
                @if (hold.owner_unreachable) { <span class="wrk-chip wrk-chip-blocked" title="Its session is silent; the slot stays held until it expires or is released.">owner unreachable</span> }
                <span class="wrk-sub wrk-grow">{{ hold.purpose }}</span>
                @if (hold.item) { <a [routerLink]="['/work/items', hold.item]">{{ hold.item }}</a> }
              </div>
              <div class="wrk-row">
                <span class="wrk-sub">expires {{ hold.expires_at | ago }}</span>
                <button type="button" class="wrk-btn" [disabled]="busy()" (click)="renew(hold)">Renew</button>
                <button type="button" class="wrk-btn" [disabled]="busy()" (click)="release(hold)">Release</button>
              </div>
            </li>
          }
          @for (hold of resource.waiters ?? []; track hold.id) {
            <li>
              <div class="wrk-row">
                <wrk-actor [actor]="hold.holder" />
                <span class="wrk-chip wrk-chip-open">waiting #{{ hold.queue_position }}</span>
                <span class="wrk-sub wrk-grow">{{ hold.purpose }} ({{ hold.priority }})</span>
                <button type="button" class="wrk-btn" [disabled]="busy()" (click)="release(hold)">Leave queue</button>
              </div>
            </li>
          }
        </ul>
      </section>
    }
    <details class="wrk-acquire">
      <summary>Take a resource</summary>
      <form class="wrk-form" (ngSubmit)="acquire()">
        <div class="wrk-form-row">
          <label>Resource <input class="wrk-input" name="resource" [(ngModel)]="resource" required placeholder="gpu-0" /></label>
          <label>Purpose <input class="wrk-input" name="purpose" [(ngModel)]="purpose" required /></label>
        </div>
        <div class="wrk-form-row">
          <label>Hold for (s) <input class="wrk-input" name="ttl" type="number" min="1" max="86400" [(ngModel)]="ttl" /></label>
          <label>Wait up to (s) <input class="wrk-input" name="wait" type="number" min="0" max="55" [(ngModel)]="wait" /></label>
          <label>Capacity (new) <input class="wrk-input" name="capacity" type="number" min="1" [(ngModel)]="capacity" /></label>
          <label>Priority
            <select class="wrk-input" name="priority" [(ngModel)]="priority">@for (p of priorities; track p) { <option [value]="p">{{ p }}</option> }</select>
          </label>
          <label>Item (optional) <input class="wrk-input" name="item" [(ngModel)]="item" placeholder="SW-12" /></label>
        </div>
        <div class="wrk-row"><button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !resource.trim() || !purpose.trim()">Acquire</button></div>
      </form>
    </details>
  `,
  styles: [`.wrk-resource { padding: 8px 0; border-top: 1px solid var(--hairline); } .wrk-resource:first-of-type { border-top: 0; } .wrk-acquire { margin-top: 10px; } .wrk-acquire summary { cursor: pointer; color: var(--accent-ink); }`]
})
export class ResourcesComponent extends WorkPageBase {
  readonly project = input.required<string>();
  readonly resources = input.required<ResourceView[]>();
  readonly changed = output<void>();
  readonly priorities = WORK_PRIORITIES;
  resource = '';
  purpose = '';
  ttl = 3600;
  wait = 0;
  capacity = 1;
  priority: WorkPriority = 'medium';
  item = '';

  constructor() {
    super();
  }

  protected reload(): void {
    // Driven by the parent.
  }

  async acquire(): Promise<void> {
    const body: Record<string, unknown> = {
      project: this.project(),
      resource: this.resource.trim(),
      purpose: this.purpose.trim(),
      ttl_seconds: this.ttl,
      priority: this.priority,
      idempotency_key: idempotencyKey('ui-acquire')
    };
    if (this.wait > 0) body['wait_seconds'] = this.wait;
    if (this.capacity > 1) body['capacity'] = this.capacity;
    if (this.item.trim()) body['item'] = this.item.trim();
    const result = await this.run(() => this.api.acquire(body));
    if (result) {
      this.notice.set(result.outcome === 'granted' ? `Granted ${result.resource.name}.` : `${result.resource.name} is held; you are queued.`);
      this.changed.emit();
    }
  }

  async renew(hold: HoldView): Promise<void> {
    if (await this.run(() => this.api.renewHold(hold.id), 'Renewed.')) this.changed.emit();
  }

  async release(hold: HoldView): Promise<void> {
    if (await this.run(() => this.api.releaseHold(hold.id), 'Released.')) this.changed.emit();
  }
}
