import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { changeLines, changeVerb } from './work-format';
import { ActorComponent, AgoPipe } from './work-shared';
import { ItemView, SessionView, WorkChange } from './work.models';

/** The live sessions of a project: who, what for, where, the items each holds and its last write. */
@Component({
  selector: 'wrk-session-list',
  standalone: true,
  imports: [RouterLink, ActorComponent, AgoPipe],
  template: `
    @if (sessions().length === 0) { <div class="wrk-empty">No agent is connected.</div> }
    <ul class="wrk-list">
      @for (session of sessions(); track session.id) {
        <li>
          <div class="wrk-row">
            <wrk-actor [actor]="{ name: session.identity, kind: session.identity_kind === 'agent' ? 'agent' : 'session', session_id: session.id, harness: session.harness, model: session.model, host: session.host }" />
            <span class="wrk-sub wrk-grow" style="text-align:right">seen {{ session.last_seen_at | ago }}</span>
          </div>
          @if (session.intent) { <div>{{ session.intent }}</div> }
          <div class="wrk-sub">
            @if (session.repository) { {{ session.repository }}@if (session.branch) { · {{ session.branch }} } }
          </div>
          @for (item of heldBy(session.id); track item.key) {
            <div class="wrk-row">holds <a [routerLink]="['/work/items', item.key]">{{ item.key }}</a> <span class="wrk-clip">{{ item.title }}</span></div>
          }
          @if (lastAction(session.id); as last) {
            <div class="wrk-sub">last: {{ verb(last) }} <a [routerLink]="['/work/items', last.entity_key]">{{ last.entity_key }}</a>
              @for (line of lines(last); track line.field) { · {{ line.field }} → {{ line.new }} } · {{ last.created_at | ago }}</div>
          }
        </li>
      }
    </ul>
  `
})
export class SessionListComponent {
  readonly sessions = input.required<SessionView[]>();
  /** The project's claimed items; each session shows those it holds. */
  readonly claimed = input<ItemView[]>([]);
  /** The project's recent writes, oldest first; each session shows its newest. */
  readonly feed = input<WorkChange[]>([]);
  readonly verb = changeVerb;
  readonly lines = (change: WorkChange) => changeLines(change, 2);

  private readonly lastActions = computed(() => {
    const bySession = new Map<string, WorkChange>();
    for (const change of this.feed()) {
      if (change.actor.session_id) bySession.set(change.actor.session_id, change);
    }
    return bySession;
  });

  heldBy(sessionId: string): ItemView[] {
    return this.claimed().filter(item => item.claim?.session_id === sessionId);
  }

  lastAction(sessionId: string): WorkChange | undefined {
    return this.lastActions().get(sessionId);
  }
}
