import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ErrorComponent, KeyHolder, WorkPageBase } from './work-base';
import { ActorComponent, AgoPipe } from './work-shared';
import { MESSAGE_KINDS, MessageKind, MessageView } from './work.models';

/**
 * A list of messages with the human's actions on each: answer or decline an open question or
 * request (post with in_reply_to and resolves), acknowledge (ack), reply. Every call is a REST
 * route the agents use, attributed to the signed-in person.
 */
@Component({
  selector: 'wrk-message-list',
  standalone: true,
  imports: [FormsModule, RouterLink, ActorComponent, AgoPipe, ErrorComponent],
  template: `
    <wrk-error [error]="error()" [reloadable]="false" />
    @if (messages().length === 0) { <div class="wrk-empty">{{ empty() }}</div> }
    <div class="wrk-list-gap">
      @for (message of messages(); track message.id) {
        <article class="wrk-msg" [attr.data-kind]="message.kind" [attr.data-blocking]="message.blocking ? 'true' : null" [attr.data-reply]="message.in_reply_to ? 'true' : null">
          <div class="wrk-row">
            <wrk-actor [actor]="message.from" />
            <span class="wrk-chip">{{ message.kind }}</span>
            @if (message.blocking) { <span class="wrk-chip wrk-chip-blocked" data-reason="awaiting_human">blocks {{ message.item }}</span> }
            @if (message.state === 'open') { <span class="wrk-chip wrk-chip-open">open</span> }
            @if (message.state === 'answered' || message.state === 'declined') { <span class="wrk-chip">{{ message.state }}</span> }
            <span class="wrk-sub">{{ channelLabel(message) }}</span>
            <span class="wrk-sub wrk-grow" style="text-align:right">{{ message.created_at | ago }}</span>
          </div>
          <p class="wrk-pre">{{ message.body }}</p>
          @if (message.item && showItem()) { <div class="wrk-sub">About <a [routerLink]="['/work/items', message.item]">{{ message.item }}</a></div> }
          @if (message.resolved_by) {
            <div class="wrk-sub">{{ message.state }} by <wrk-actor [actor]="message.resolved_by" /> {{ message.resolved_at | ago }}</div>
          }
          <div class="wrk-row">
            @if (message.state === 'open') {
              <button type="button" class="wrk-btn" (click)="open(message, 'answered')">Answer</button>
              <button type="button" class="wrk-btn" (click)="open(message, 'declined')">Decline</button>
            }
            <button type="button" class="wrk-btn wrk-btn-quiet" (click)="open(message, 'reply')">Reply</button>
            @if (acknowledgeable()) { <button type="button" class="wrk-btn wrk-btn-quiet" [disabled]="busy()" (click)="acknowledge(message)">Mark read</button> }
          </div>
          @if (replying()?.message?.id === message.id) {
            <div class="wrk-form">
              <textarea class="wrk-input" rows="3" [(ngModel)]="reply" [attr.aria-label]="replying()!.mode === 'reply' ? 'Reply' : replying()!.mode === 'answered' ? 'Answer' : 'Reason for declining'"
                [placeholder]="replying()!.mode === 'declined' ? 'Why you decline (required)' : 'Your message'"></textarea>
              <div class="wrk-row">
                <button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !reply.trim()" (click)="send(message)">
                  {{ replying()!.mode === 'reply' ? 'Send reply' : replying()!.mode === 'answered' ? 'Send answer' : 'Decline' }}
                </button>
                <button type="button" class="wrk-btn wrk-btn-quiet" (click)="replying.set(null)">Cancel</button>
              </div>
            </div>
          }
        </article>
      }
    </div>
  `,
  styles: [`.wrk-list-gap { display: flex; flex-direction: column; gap: 8px; }`]
})
export class MessageListComponent extends WorkPageBase {
  readonly messages = input.required<MessageView[]>();
  readonly project = input.required<string>();
  readonly empty = input('Nothing here yet.');
  readonly showItem = input(true);
  /** Show "Mark read" (ack): for messages delivered to the humans. */
  readonly acknowledgeable = input(false);
  readonly changed = output<void>();
  readonly replying = signal<{ message: MessageView; mode: 'answered' | 'declined' | 'reply' } | null>(null);
  reply = '';
  private readonly replyKey = new KeyHolder('ui-reply');

  constructor() {
    super();
  }

  protected reload(): void {
    // Driven by the parent.
  }

  channelLabel(message: MessageView): string {
    if (message.channel === 'item') return `thread of ${message.item}`;
    if (message.channel === 'feature') return `feature ${message.feature}`;
    if (message.channel === 'resource') return `resource ${message.resource}`;
    if (message.channel === 'session') return 'direct to a session';
    if (message.channel === 'human') return 'to the humans';
    return message.channel;
  }

  open(message: MessageView, mode: 'answered' | 'declined' | 'reply'): void {
    this.reply = '';
    this.replyKey.reset();
    this.replying.set({ message, mode });
  }

  async send(message: MessageView): Promise<void> {
    const mode = this.replying()?.mode ?? 'reply';
    const body: Record<string, unknown> = { project: this.project(), body: this.reply.trim(), in_reply_to: message.id, idempotency_key: this.replyKey.key };
    if (message.item) body['item'] = message.item;
    else if (message.feature) body['feature'] = message.feature;
    else if (message.resource) body['resource'] = message.resource;
    else if (message.from.session_id) body['to_session'] = message.from.session_id;
    else body['to_human'] = true;
    if (mode !== 'reply') body['resolves'] = mode;
    const done = await this.run(() => this.api.post(body));
    if (done) {
      this.replyKey.reset();
      this.replying.set(null);
      this.changed.emit();
    }
  }

  async acknowledge(message: MessageView): Promise<void> {
    const done = await this.run(() => this.api.ack({ project: this.project(), messages: [message.id] }));
    if (done) this.changed.emit();
  }
}

/** Posts a message: to the project, a feature, an item's thread, a resource channel, a session or the humans. */
@Component({
  selector: 'wrk-message-composer',
  standalone: true,
  imports: [FormsModule, ErrorComponent],
  template: `
    <wrk-error [error]="error()" [reloadable]="false" />
    <form class="wrk-form" (ngSubmit)="send()">
      <div class="wrk-form-row">
        <label>Channel
          <select class="wrk-input" name="channel" [(ngModel)]="channel">
            @for (option of channels(); track option.value) { <option [value]="option.value">{{ option.label }}</option> }
          </select>
        </label>
        <label>Kind
          <select class="wrk-input" name="kind" [(ngModel)]="kind">
            @for (option of kinds; track option) { <option [value]="option">{{ option }}</option> }
          </select>
        </label>
        @if (channel === 'item' || channel === 'feature' || channel === 'resource' || channel === 'session') {
          <label>{{ channel === 'item' ? 'Item key' : channel === 'feature' ? 'Feature key' : channel === 'resource' ? 'Resource name' : 'Session id' }}
            <input class="wrk-input" name="target" [(ngModel)]="target" [placeholder]="channel === 'item' ? 'SW-12' : channel === 'feature' ? 'SW-F2' : ''" />
          </label>
        }
      </div>
      @if (item() && channel === 'human' && kind === 'question') {
        <label style="display:flex;gap:6px;align-items:center"><input type="checkbox" name="blocking" [(ngModel)]="blocking" /> Blocks {{ item() }} until answered (a last resort)</label>
      }
      <label>Message
        <textarea class="wrk-input" name="body" rows="3" [(ngModel)]="body" placeholder="Messages are data: agents decide what to do with them."></textarea>
      </label>
      <div class="wrk-row">
        <button type="submit" class="wrk-btn wrk-btn-primary" [disabled]="busy() || !body.trim()">Post</button>
        @if (notice()) { <span class="wrk-sub">{{ notice() }}</span> }
      </div>
    </form>
  `
})
export class MessageComposerComponent extends WorkPageBase {
  readonly project = input.required<string>();
  /** Fixed target for a composer that lives on an item or feature page. */
  readonly item = input<string | undefined>();
  readonly posted = output<void>();
  readonly kinds = MESSAGE_KINDS;
  readonly channels = computed(() =>
    this.item()
      ? [{ value: 'item', label: 'This item\'s thread' }, { value: 'human', label: 'To the humans' }]
      : [
          { value: 'project', label: 'Whole project' },
          { value: 'feature', label: 'A feature' },
          { value: 'item', label: 'An item\'s thread' },
          { value: 'resource', label: 'A resource' },
          { value: 'session', label: 'A session' }
        ]);
  channel = 'project';
  kind: MessageKind = 'info';
  target = '';
  body = '';
  blocking = false;
  private readonly postKey = new KeyHolder('ui-post');

  constructor() {
    super();
  }

  protected reload(): void {
    // Nothing to reload.
  }

  ngOnInit(): void {
    if (this.item()) this.channel = 'item';
  }

  async send(): Promise<void> {
    const request: Record<string, unknown> = { project: this.project(), body: this.body.trim(), kind: this.kind, idempotency_key: this.postKey.key };
    const target = this.target.trim();
    if (this.item()) request['item'] = this.item();
    if (this.channel === 'item' && !this.item()) request['item'] = target;
    else if (this.channel === 'feature') request['feature'] = target;
    else if (this.channel === 'resource') request['resource'] = target;
    else if (this.channel === 'session') request['to_session'] = target;
    else if (this.channel === 'human') {
      request['to_human'] = true;
      if (this.blocking && this.kind === 'question' && this.item()) request['blocking'] = true;
    }
    const done = await this.run(() => this.api.post(request), 'Posted.');
    if (done) {
      this.postKey.reset();
      this.body = '';
      this.posted.emit();
    }
  }
}
