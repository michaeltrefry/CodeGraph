import { Component, OnDestroy, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ActivityComponent } from './work-activity';
import { ErrorComponent, WorkContext, WorkPageBase } from './work-base';
import { MessageListComponent } from './work-messages-ui';
import { ResourcesComponent } from './work-resources-ui';
import { ActorComponent, AgoPipe } from './work-shared';
import { withDebounce } from './work-live.service';
import { changeLines, changeVerb } from './work-format';
import { ItemView, MessageView, ResourceView, SessionView, WorkChange } from './work.models';

/** Now: who is working, what is held, and what waits on a person. */
@Component({
  selector: 'app-work-now',
  standalone: true,
  imports: [FormsModule, RouterLink, ActorComponent, AgoPipe, ErrorComponent, MessageListComponent, ResourcesComponent, ActivityComponent],
  template: `
    <div class="wrk-page">
      <div class="wrk-head">
        <h1>Now</h1>
        <label class="wrk-sr" for="wrk-project">Project</label>
        <select id="wrk-project" class="wrk-input" [ngModel]="ctx.selected()" (ngModelChange)="ctx.select($event)">
          @for (project of ctx.projects(); track project.id) { <option [value]="project.key">{{ project.key }} · {{ project.name }}</option> }
        </select>
        <span class="wrk-live" [attr.data-state]="feedError() ? 'off' : 'on'" [title]="feedError() || 'Following the change feed'">{{ feedError() ? 'reconnecting' : 'live' }}</span>
        <span class="wrk-grow"></span>
        @if (ctx.selected()) {
          <a class="wrk-btn" [routerLink]="['/work/projects', ctx.selected()]">Project</a>
          <a class="wrk-btn" routerLink="/work/messages">Message board</a>
        }
      </div>

      <wrk-error [error]="error()" (reload)="reload()" />
      @if (ctx.error()) { <div class="wrk-error">{{ ctx.error() }}</div> }
      @if (ctx.loaded() && ctx.projects().length === 0) {
        <div class="wrk-card"><div class="wrk-empty">There are no work projects yet. Create one from the <a routerLink="/work/projects/new">Project page</a>, or have an agent call create_work.</div></div>
      }

      @if (ctx.selected()) {
        <div class="wrk-grid">
          <section class="wrk-card" [class.wrk-attention]="blockedOnYou().length > 0" aria-labelledby="wrk-h-you">
            <h2 id="wrk-h-you">Blocked on you <span class="wrk-count">{{ blockedOnYou().length }}</span></h2>
            <wrk-message-list [messages]="blockedOnYou()" [project]="ctx.selected()" [acknowledgeable]="true" empty="Nothing is waiting on you." (changed)="reload()" />
          </section>

          <section class="wrk-card" aria-labelledby="wrk-h-sessions">
            <h2 id="wrk-h-sessions">Live sessions <span class="wrk-count">{{ sessions().length }}</span></h2>
            @if (sessions().length === 0) { <div class="wrk-empty">No agent is connected.</div> }
            <ul class="wrk-list">
              @for (session of sessions(); track session.id) {
                <li>
                  <div class="wrk-row">
                    <wrk-actor [actor]="{ name: session.identity, kind: 'session', session_id: session.id, harness: session.harness, model: session.model, host: session.host }" />
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
          </section>

          <section class="wrk-card" aria-labelledby="wrk-h-holds">
            <h2 id="wrk-h-holds">Holds and waiters</h2>
            <wrk-resources [project]="ctx.selected()" [resources]="resources()" (changed)="reload()" />
          </section>

          <section class="wrk-card" aria-labelledby="wrk-h-activity">
            <h2 id="wrk-h-activity">Recent activity</h2>
            <wrk-activity [entries]="feed()" [limit]="20" />
          </section>
        </div>
      }
    </div>
  `
})
export class WorkNowComponent extends WorkPageBase implements OnInit, OnDestroy {
  readonly ctx = inject(WorkContext);
  readonly sessions = signal<SessionView[]>([]);
  readonly resources = signal<ResourceView[]>([]);
  readonly blockedOnYou = signal<MessageView[]>([]);
  readonly claimed = signal<ItemView[]>([]);
  readonly verb = changeVerb;
  readonly lines = (change: WorkChange) => changeLines(change, 2);
  private stopStream?: () => void;
  private readonly refresh = withDebounce(() => void this.reload(), 300);

  readonly lastActions = computed(() => {
    const bySession = new Map<string, WorkChange>();
    for (const change of this.feed()) {
      if (change.actor.session_id) bySession.set(change.actor.session_id, change);
    }
    return bySession;
  });

  constructor() {
    super();
    effect(() => {
      const key = this.ctx.selected();
      untracked(() => this.switchTo(key));
    });
  }

  async ngOnInit(): Promise<void> {
    await this.ctx.load();
  }

  ngOnDestroy(): void {
    this.stopStream?.();
    this.refresh.cancel();
  }

  heldBy(sessionId: string): ItemView[] {
    return this.claimed().filter(item => item.claim?.session_id === sessionId);
  }

  lastAction(sessionId: string): WorkChange | undefined {
    return this.lastActions().get(sessionId);
  }

  private switchTo(key: string): void {
    this.stopStream?.();
    this.unfollow();
    this.sessions.set([]);
    this.resources.set([]);
    this.blockedOnYou.set([]);
    this.claimed.set([]);
    this.feed.set([]);
    if (!key) return;
    this.follow(key);
    this.stopStream = this.live.streamMessages(key, () => this.refresh.call(), undefined, () => this.refresh.call());
    void this.reload();
  }

  protected async reload(): Promise<void> {
    const key = this.ctx.selected();
    if (!key) return;
    this.error.set(null);
    try {
      const [sessions, resources, messages, claimed] = await Promise.all([
        this.api.sessions(key),
        this.api.resources(key),
        this.api.messages(key, { open: true, toHuman: true, limit: 50 }),
        this.api.items(key, { claimed: true, status: ['ready', 'in_progress', 'in_review', 'backlog'] }),
        this.loadRecent(key, 40)
      ]);
      if (key !== this.ctx.selected()) return;
      this.sessions.set(sessions);
      this.resources.set(resources);
      this.blockedOnYou.set(messages.items);
      this.claimed.set(claimed);
    } catch (error) {
      await this.run(() => Promise.reject(error));
    }
  }
}
