import { Component, OnDestroy, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ErrorComponent, WorkContext, WorkPageBase } from './work-base';
import { withDebounce } from './work-live.service';
import { MessageComposerComponent, MessageListComponent } from './work-messages-ui';
import { MessageView } from './work.models';

type Channel = '' | 'project' | 'feature' | 'item' | 'resource' | 'session' | 'human';

/** Message board: per-project channels you can read, post to, answer and decline in. Updates over the message stream. */
@Component({
  selector: 'app-work-messages',
  standalone: true,
  imports: [FormsModule, RouterLink, ErrorComponent, MessageListComponent, MessageComposerComponent],
  template: `
    <div class="wrk-page">
      <div class="wrk-head">
        <h1>Message board</h1>
        <label class="wrk-sr" for="wrk-project">Project</label>
        <select id="wrk-project" class="wrk-input" [ngModel]="ctx.selected()" (ngModelChange)="ctx.select($event)">
          @for (project of ctx.projects(); track project.id) { <option [value]="project.key">{{ project.key }} · {{ project.name }}</option> }
        </select>
        <span class="wrk-live" [attr.data-state]="connected() ? 'on' : 'off'">{{ connected() ? 'live' : 'reconnecting' }}</span>
        <span class="wrk-grow"></span>
        <a class="wrk-btn" routerLink="/work">Now</a>
      </div>
      <wrk-error [error]="error()" (reload)="load(true)" />

      @if (ctx.selected()) {
        <div class="wrk-grid-2">
          <div>
            <div class="wrk-tabs" role="tablist" aria-label="Channels">
              @for (c of channels; track c.id) {
                <button type="button" role="tab" class="wrk-tab" [attr.aria-selected]="channel() === c.id" (click)="setChannel(c.id)">{{ c.label }}</button>
              }
            </div>
            <div class="wrk-filters">
              <label><input type="checkbox" [ngModel]="openOnly()" (ngModelChange)="openOnly.set($event); load(true)" /> open questions and requests only</label>
              @if (channel() === 'item' || channel() === 'feature' || channel() === 'resource') {
                <input class="wrk-input" [attr.aria-label]="channel() + ' filter'" [placeholder]="channel() === 'item' ? 'Item key, e.g. SW-12' : channel() === 'feature' ? 'Feature key, e.g. SW-F2' : 'Resource name'" [(ngModel)]="subject" (change)="load(true)" />
              }
            </div>
            <wrk-message-list [messages]="messages()" [project]="ctx.selected()" [acknowledgeable]="channel() === 'human'" empty="No messages in this channel." (changed)="load(true)" />
            @if (more()) { <div class="wrk-row" style="margin-top:8px"><button type="button" class="wrk-btn" (click)="loadMore()">Older messages</button></div> }
          </div>
          <div>
            <section class="wrk-card" aria-labelledby="wrk-h-post">
              <h2 id="wrk-h-post">Post</h2>
              <wrk-message-composer [project]="ctx.selected()" (posted)="load(true)" />
            </section>
            <section class="wrk-card" style="margin-top:14px" aria-labelledby="wrk-h-inbox">
              <h2 id="wrk-h-inbox">Human inbox <span class="wrk-count">{{ inbox().length }} unread</span></h2>
              <wrk-message-list [messages]="inbox()" [project]="ctx.selected()" [acknowledgeable]="true" empty="Nothing unread." (changed)="load(true)" />
            </section>
          </div>
        </div>
      }
    </div>
  `
})
export class WorkMessagesComponent extends WorkPageBase implements OnInit, OnDestroy {
  readonly ctx = inject(WorkContext);
  readonly channels: { id: Channel; label: string }[] = [
    { id: '', label: 'All' },
    { id: 'project', label: 'Project' },
    { id: 'feature', label: 'Features' },
    { id: 'item', label: 'Items' },
    { id: 'resource', label: 'Resources' },
    { id: 'session', label: 'Sessions' },
    { id: 'human', label: 'To the humans' }
  ];
  readonly channel = signal<Channel>('');
  readonly openOnly = signal(false);
  readonly messages = signal<MessageView[]>([]);
  readonly inbox = signal<MessageView[]>([]);
  readonly more = signal('');
  readonly connected = signal(false);
  subject = '';
  private stopStream?: () => void;
  private readonly refresh = withDebounce(() => void this.reload(), 250);

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

  setChannel(channel: Channel): void {
    this.channel.set(channel);
    this.subject = '';
    void this.load(true);
  }

  private switchTo(key: string): void {
    this.stopStream?.();
    this.messages.set([]);
    this.inbox.set([]);
    this.more.set('');
    if (!key) return;
    this.stopStream = this.live.streamMessages(key, () => this.refresh.call(), state => this.connected.set(state), () => this.refresh.call());
    void this.load(true);
  }

  private query(before?: string) {
    const channel = this.channel();
    const subject = this.subject.trim();
    return {
      channel: channel || undefined,
      item: channel === 'item' && subject ? subject : undefined,
      feature: channel === 'feature' && subject ? subject : undefined,
      resource: channel === 'resource' && subject ? subject : undefined,
      open: this.openOnly() || undefined,
      before,
      limit: 50
    };
  }

  protected reload(): Promise<void> {
    return this.load(false);
  }

  /**
   * Reads the first page and the inbox. A refresh from the stream merges what is new at the head of
   * the pages already loaded; an action, a filter or a channel change starts the list over.
   */
  async load(reset: boolean): Promise<void> {
    const key = this.ctx.selected();
    if (!key) return;
    this.error.set(null);
    const result = await this.run(async () => {
      const [page, inbox] = await Promise.all([this.api.messages(key, this.query()), this.api.inbox(key, { limit: 50 })]);
      return { page, inbox };
    });
    if (result && key === this.ctx.selected()) {
      if (reset || this.messages().length === 0) {
        this.messages.set(result.page.items);
        this.more.set(result.page.next_before ?? '');
      } else {
        const fresh = new Set(result.page.items.map(message => message.id));
        this.messages.update(current => [...result.page.items, ...current.filter(message => !fresh.has(message.id))]);
      }
      this.inbox.set(result.inbox.items);
    }
  }

  async loadMore(): Promise<void> {
    const key = this.ctx.selected();
    const page = await this.run(() => this.api.messages(key, this.query(this.more())));
    if (page) {
      this.messages.update(current => [...current, ...page.items]);
      this.more.set(page.next_before ?? '');
    }
  }
}
