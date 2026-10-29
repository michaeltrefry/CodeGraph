import { Component, Pipe, PipeTransform, computed, input } from '@angular/core';
import { BlockedReason, Holder, WorkActor, WorkStatus } from './work.models';

export function timeAgo(value: string | undefined, now = Date.now()): string {
  if (!value) return '';
  const then = Date.parse(value.endsWith('Z') || /[+-]\d\d:?\d\d$/.test(value) ? value : `${value}Z`);
  if (Number.isNaN(then)) return value;
  const seconds = Math.round((now - then) / 1000);
  if (seconds < -5) {
    const ahead = -seconds;
    return ahead < 90 ? `in ${ahead}s` : ahead < 5400 ? `in ${Math.round(ahead / 60)}m` : `in ${Math.round(ahead / 3600)}h`;
  }
  if (seconds < 10) return 'just now';
  if (seconds < 90) return `${seconds}s ago`;
  if (seconds < 5400) return `${Math.round(seconds / 60)}m ago`;
  if (seconds < 129_600) return `${Math.round(seconds / 3600)}h ago`;
  return `${Math.round(seconds / 86_400)}d ago`;
}

@Pipe({ name: 'ago' })
export class AgoPipe implements PipeTransform {
  transform(value: string | undefined): string {
    return timeAgo(value);
  }
}

export function statusLabel(status: WorkStatus): string {
  return status.replace('_', ' ');
}

export function blockedLabel(reason: BlockedReason | undefined): string {
  switch (reason) {
    case 'awaiting_human': return 'awaiting you';
    case 'cancelled_prerequisite': return 'cancelled prerequisite';
    case 'waiting': return 'waiting';
    default: return 'blocked';
  }
}

/** Who did it: a person, or an agent session with its harness, model and host (E3). */
@Component({
  selector: 'wrk-actor',
  standalone: true,
  template: `
    <span class="wrk-actor" [class.wrk-actor-agent]="isAgent()" [attr.title]="title()">
      <svg class="wrk-actor-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true">
        @if (isAgent()) {
          <rect x="3" y="4.5" width="10" height="8" rx="2"/><path d="M8 4.5V2.5M6 8v1M10 8v1"/>
        } @else {
          <circle cx="8" cy="5.5" r="2.5"/><path d="M3 13.5c.6-2.4 2.5-3.5 5-3.5s4.4 1.1 5 3.5"/>
        }
      </svg>
      <span class="wrk-actor-name">{{ name() }}</span>
      @if (detail()) { <span class="wrk-actor-detail">{{ detail() }}</span> }
    </span>
  `
})
export class ActorComponent {
  readonly actor = input<Pick<WorkActor, 'name'> & Partial<WorkActor> | Holder | undefined>();
  readonly name = computed(() => {
    const value = this.actor();
    if (!value) return 'unknown';
    return 'name' in value ? value.name : value.identity;
  });
  readonly isAgent = computed(() => {
    const value = this.actor();
    return !!value && (('kind' in value && value.kind === 'session') || !!value.session_id || !!value.harness);
  });
  readonly detail = computed(() => {
    const value = this.actor();
    if (!value) return '';
    const parts = [value.harness, value.model].filter(Boolean).join(' / ');
    return value.host ? `${parts}${parts ? ' @ ' : ''}${value.host}` : parts;
  });
  readonly title = computed(() => {
    const value = this.actor();
    const session = value?.session_id ? ` · session ${value.session_id}` : '';
    return `${this.isAgent() ? 'Agent' : 'Person'} ${this.name()}${this.detail() ? ` (${this.detail()})` : ''}${session}`;
  });
}

@Component({
  selector: 'wrk-status',
  standalone: true,
  template: `
    <span class="wrk-chip" [attr.data-status]="status()">{{ label() }}</span>
    @if (blocked() && reason()) {
      <span class="wrk-chip wrk-chip-blocked" [attr.data-reason]="reason()">{{ blockedText() }}</span>
    }
  `
})
export class StatusComponent {
  readonly status = input.required<WorkStatus>();
  readonly blocked = input(false);
  readonly reason = input<BlockedReason | undefined>();
  readonly label = computed(() => statusLabel(this.status()));
  readonly blockedText = computed(() => blockedLabel(this.reason()));
}
