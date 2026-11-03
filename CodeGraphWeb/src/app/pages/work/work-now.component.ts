import { Component, OnDestroy, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ActivityComponent } from './work-activity';
import { ErrorComponent, WorkContext, WorkPageBase } from './work-base';
import { MessageListComponent } from './work-messages-ui';
import { ResourcesComponent } from './work-resources-ui';
import { SessionListComponent } from './work-sessions-ui';
import { withDebounce } from './work-live.service';
import { ItemView, MessageView, ResourceView, SessionView } from './work.models';

/** Now: who is working, what is held, and what waits on a person. */
@Component({
  selector: 'app-work-now',
  standalone: true,
  imports: [FormsModule, RouterLink, ErrorComponent, MessageListComponent, ResourcesComponent, ActivityComponent, SessionListComponent],
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
          <a class="wrk-btn" [routerLink]="['/projects', ctx.selected(), 'overview']">Project</a>
          <a class="wrk-btn" routerLink="/work/messages">Message board</a>
        }
      </div>

      <wrk-error [error]="error()" (reload)="reload()" />
      @if (ctx.error()) { <div class="wrk-error">{{ ctx.error() }}</div> }
      @if (ctx.loaded() && ctx.projects().length === 0) {
        <div class="wrk-card"><div class="wrk-empty">There are no work projects yet. Create one from the <a routerLink="/projects" [queryParams]="{ new: 1 }">Projects page</a>, or have an agent call create_work.</div></div>
      }

      @if (ctx.selected()) {
        <div class="wrk-grid">
          <section class="wrk-card" [class.wrk-attention]="blockedOnYou().length > 0" aria-labelledby="wrk-h-you">
            <h2 id="wrk-h-you">Blocked on you <span class="wrk-count">{{ blockedOnYou().length }}</span></h2>
            <wrk-message-list [messages]="blockedOnYou()" [project]="ctx.selected()" [acknowledgeable]="true" empty="Nothing is waiting on you." (changed)="reload()" />
          </section>

          <section class="wrk-card" aria-labelledby="wrk-h-sessions">
            <h2 id="wrk-h-sessions">Live sessions <span class="wrk-count">{{ sessions().length }}</span></h2>
            <wrk-session-list [sessions]="sessions()" [claimed]="claimed()" [feed]="feed()" />
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
  private stopStream?: () => void;
  private readonly refresh = withDebounce(() => void this.reload(), 300);

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
