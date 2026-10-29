import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ErrorComponent, WorkPageBase, lines } from './work-base';
import { DialogComponent, ItemFormComponent } from './work-forms';
import { buildTimeline, pushCommits, shortSha } from './work-format';
import { withDebounce } from './work-live.service';
import { MessageComposerComponent, MessageListComponent } from './work-messages-ui';
import { ActorComponent, AgoPipe, StatusComponent } from './work-shared';
import { GitHubEvidence, ItemView, MessageView, WORK_STATUSES, WorkApiError, WorkLink, WorkStatus } from './work.models';

/** Item detail: the brief, acceptance, timeline, commits and PRs, messages, handoffs, attempts, and every write an agent can make. */
@Component({
  selector: 'app-work-item',
  standalone: true,
  imports: [FormsModule, RouterLink, ActorComponent, AgoPipe, StatusComponent, ErrorComponent, DialogComponent, ItemFormComponent, MessageListComponent, MessageComposerComponent],
  template: `
    <div class="wrk-page">
      @if (item(); as it) {
        <div class="wrk-head">
          <div class="wrk-grow">
            <div class="wrk-key">
              <a [routerLink]="['/work/projects', it.project_key]">{{ it.project_key }}</a>
              @if (it.feature) { · <a [routerLink]="['/work/features', it.feature.key]">{{ it.feature.key }} {{ it.feature.title }}</a> }
              · {{ it.key }}@if (it.ref) { ({{ it.ref }}) } · v{{ it.version }}
            </div>
            <h1>{{ it.title }}</h1>
          </div>
          <wrk-status [status]="it.status" [blocked]="it.blocked" [reason]="it.blocked_reason" />
          <span class="wrk-chip">{{ it.priority }}</span><span class="wrk-chip">{{ it.type }}</span>
          <span class="wrk-live" [attr.data-state]="feedError() ? 'off' : 'on'">{{ feedError() ? 'reconnecting' : 'live' }}</span>
          <button type="button" class="wrk-btn" (click)="dialog.set('edit')">Edit</button>
        </div>
        <wrk-error [error]="error()" (reload)="reload()" />
        @if (notice()) { <div class="wrk-ok">{{ notice() }}</div> }
        @if (it.merged_awaiting_acceptance) { <div class="wrk-conflict">A merge was seen on this item. It stays {{ it.status.replace('_', ' ') }} until someone reports acceptance and completes it.</div> }

        <div class="wrk-grid-2">
          <div style="display:flex;flex-direction:column;gap:14px">
            <section class="wrk-card" aria-labelledby="wrk-h-brief">
              <h2 id="wrk-h-brief">Brief</h2>
              @if (it.goal) { <p class="wrk-pre"><strong>Goal.</strong> {{ it.goal }}</p> }
              @if (it.description) { <p class="wrk-pre">{{ it.description }}</p> }
              <div class="wrk-row">
                @if (it.repository) { <span class="wrk-chip">{{ it.repository }}</span> }
                @for (label of it.labels; track label) { <span class="wrk-chip">{{ label }}</span> }
              </div>
              @if ((it.touches ?? []).length > 0) { <p class="wrk-sub">Touches: @for (t of it.touches; track t) { <code>{{ t }}</code>{{ ' ' }} }</p> }
              @if ((it.inherited_requirements ?? []).length > 0) {
                <h3>Inherits</h3>
                <ul>@for (r of it.inherited_requirements; track r.id) { <li><strong>{{ r.id }}</strong> {{ r.text }}</li> }</ul>
              }
              <p class="wrk-sub">Created by {{ it.created_by }} {{ it.created_at | ago }}.</p>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-ac">
              <h2 id="wrk-h-ac">Acceptance <span class="wrk-count">{{ met() }} of {{ (it.acceptance_criteria ?? []).length }} met</span></h2>
              @if ((it.acceptance_criteria ?? []).length === 0) { <div class="wrk-empty">No acceptance criteria.</div> }
              <ul class="wrk-list">
                @for (c of it.acceptance_criteria; track c.id) {
                  <li><div class="wrk-row"><span class="wrk-chip" [attr.data-status]="c.state === 'met' ? 'complete' : 'backlog'">{{ c.state }}</span><span class="wrk-key">{{ c.id }}</span><span class="wrk-grow">{{ c.text }}</span></div>
                    @if (c.evidence) { <div class="wrk-sub">Evidence: {{ c.evidence }}</div> }</li>
                }
              </ul>
              <details class="wrk-acquire">
                <summary>Report acceptance</summary>
                <form class="wrk-form" (ngSubmit)="reportAcceptance(false)">
                  <label>Summary <textarea class="wrk-input" name="summary" rows="2" [(ngModel)]="reportSummary"></textarea></label>
                  @for (c of it.acceptance_criteria; track c.id) {
                    <div class="wrk-form-row">
                      <label style="flex:0 0 100px">{{ c.id }}<select class="wrk-input" [name]="'s-' + c.id" [(ngModel)]="reportStates[c.id]"><option value="unmet">unmet</option><option value="met">met</option></select></label>
                      <label>Evidence for {{ c.id }}<input class="wrk-input" [name]="'e-' + c.id" [(ngModel)]="reportEvidence[c.id]" /></label>
                    </div>
                  }
                  <div class="wrk-row">
                    <button type="submit" class="wrk-btn" [disabled]="busy()">Record report</button>
                    <button type="button" class="wrk-btn wrk-btn-primary" [disabled]="busy() || it.status === 'complete'" (click)="reportAcceptance(true)">Record and complete</button>
                  </div>
                </form>
              </details>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-dep">
              <h2 id="wrk-h-dep">Dependencies</h2>
              <h3>Blocked by</h3>
              @if (it.blocked_by.length === 0) { <div class="wrk-empty">Nothing blocks this item.</div> }
              <ul class="wrk-list">
                @for (d of it.blocked_by; track d.key) {
                  <li><div class="wrk-row"><a [routerLink]="['/work/items', d.key]">{{ d.key }}</a><wrk-status [status]="d.status" />
                    @if (d.status === 'closed') { <span class="wrk-chip wrk-chip-blocked" data-reason="cancelled_prerequisite">cancelled prerequisite</span> }
                    <span class="wrk-grow wrk-clip">{{ d.title }}</span>
                    <button type="button" class="wrk-btn wrk-btn-quiet wrk-btn-danger" [disabled]="busy()" [attr.aria-label]="'Remove blocker ' + d.key" (click)="write({ remove_blocked_by: [d.key] }, 'Dependency removed.')">Remove</button></div></li>
                }
              </ul>
              <form class="wrk-form-row" (ngSubmit)="addBlocker()" style="margin-top:6px">
                <label>Add blocker (key)<input class="wrk-input" name="blocker" [(ngModel)]="blocker" placeholder="SW-3" /></label>
                <button type="submit" class="wrk-btn" [disabled]="busy() || !blocker.trim()">Add</button>
              </form>
              <h3 style="margin-top:10px">Blocks</h3>
              @if (it.blocks.length === 0) { <div class="wrk-empty">Nothing waits on this item.</div> }
              <div class="wrk-row">@for (d of it.blocks; track d.key) { <a [routerLink]="['/work/items', d.key]">{{ d.key }}</a> }</div>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-links">
              <h2 id="wrk-h-links">Commits, branches and pull requests</h2>
              @if (it.links.length === 0) { <div class="wrk-empty">No links.</div> }
              <ul class="wrk-list">
                @for (l of it.links; track l.id) {
                  <li><div class="wrk-row"><span class="wrk-chip">{{ l.kind }}</span><a class="wrk-grow wrk-clip" [href]="l.url" target="_blank" rel="noopener noreferrer">{{ l.url }}</a>
                    <button type="button" class="wrk-btn wrk-btn-quiet wrk-btn-danger" [disabled]="busy()" (click)="removeLink(l)">Remove</button></div></li>
                }
              </ul>
              <form class="wrk-form-row" (ngSubmit)="addLink()" style="margin-top:6px">
                <label style="flex:0 0 110px">Kind<select class="wrk-input" name="lkind" [(ngModel)]="linkKind"><option value="pr">pr</option><option value="branch">branch</option><option value="other">other</option></select></label>
                <label>URL or branch<input class="wrk-input" name="lurl" [(ngModel)]="linkUrl" /></label>
                <button type="submit" class="wrk-btn" [disabled]="busy() || !linkUrl.trim()">Add link</button>
              </form>
              <h3 style="margin-top:10px">GitHub: pushes, pull requests and reviews</h3>
              @if (evidence().length === 0) { <div class="wrk-empty" data-testid="evidence-empty">No GitHub activity recorded for this item.</div> }
              <ul class="wrk-list" data-testid="evidence">
                @for (e of evidence(); track e.id) {
                  <li><div class="wrk-row"><span class="wrk-chip">{{ e.kind.replace('_', ' ') }}</span>
                    <wrk-actor [actor]="{ name: e.author, kind: 'system' }" />
                    @if (e.review_state) { <span class="wrk-chip" [attr.data-status]="e.review_state === 'approved' ? 'complete' : 'in_review'">{{ e.review_state.replace('_', ' ') }}</span> }
                    @if (e.superseded) { <span class="wrk-chip">superseded attempt</span> }
                    @if (e.url) { <a class="wrk-grow wrk-clip" [href]="e.url" target="_blank" rel="noopener noreferrer">{{ e.repository }}@if (e.pull_request) { #{{ e.pull_request }} }</a> }
                    <span class="wrk-sub">{{ e.created_at | ago }}</span></div>
                    @if (e.kind === 'push') {
                      <ul class="wrk-commits" data-testid="commits">
                        @for (c of commitsOf(e); track $index) { <li><code>{{ c.sha }}</code> {{ c.message }}</li> }
                      </ul>
                    } @else if (e.body) { <div class="wrk-pre wrk-sub">{{ e.body }}</div> }
                    @if (e.kind === 'merge' && e.external_id) { <div class="wrk-sub">merge commit <code>{{ short(e.external_id) }}</code></div> }</li>
                }
              </ul>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-ho">
              <h2 id="wrk-h-ho">Handoffs <span class="wrk-count">{{ it.handoff_count ?? (it.handoffs ?? []).length }}</span></h2>
              @for (h of it.handoffs ?? []; track h.id) {
                <article class="wrk-msg" style="margin-bottom:6px">
                  <div class="wrk-row"><wrk-actor [actor]="{ name: h.author, kind: h.session_id ? 'session' : 'user', session_id: h.session_id, harness: h.harness }" /><span class="wrk-sub">{{ h.created_at | ago }}</span></div>
                  @if (h.done) { <div class="wrk-pre"><strong>Done.</strong> {{ h.done }}</div> }
                  @if (h.next_action) { <div class="wrk-pre"><strong>Next.</strong> {{ h.next_action }}</div> }
                  @if ((h.open_issues ?? []).length > 0) { <div><strong>Open.</strong> {{ h.open_issues!.join('; ') }}</div> }
                  @if (h.branch || h.commit) { <div class="wrk-sub"><code>{{ h.branch }}</code> <code>{{ h.commit }}</code></div> }
                </article>
              }
              @if ((it.handoffs ?? []).length === 0) { <div class="wrk-empty">No handoff recorded.</div> }
              <details class="wrk-acquire">
                <summary>Record a handoff</summary>
                <form class="wrk-form" (ngSubmit)="recordHandoff()">
                  <label>Done<textarea class="wrk-input" name="hdone" rows="2" [(ngModel)]="hDone"></textarea></label>
                  <label>Next action<input class="wrk-input" name="hnext" [(ngModel)]="hNext" /></label>
                  <label>Open issues (one per line)<textarea class="wrk-input" name="hopen" rows="2" [(ngModel)]="hOpen"></textarea></label>
                  <div class="wrk-form-row"><label>Branch<input class="wrk-input" name="hbranch" [(ngModel)]="hBranch" /></label><label>Commit<input class="wrk-input" name="hcommit" [(ngModel)]="hCommit" /></label></div>
                  <div class="wrk-row"><button type="submit" class="wrk-btn" [disabled]="busy() || (!hDone.trim() && !hNext.trim())">Record handoff</button></div>
                </form>
              </details>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-att">
              <h2 id="wrk-h-att">Attempts <span class="wrk-count">{{ it.attempt_count ?? (it.attempts ?? []).length }}</span></h2>
              @if ((it.attempts ?? []).length === 0) { <div class="wrk-empty">No execution attempt yet.</div> }
              <ul class="wrk-list">
                @for (a of it.attempts; track a.id) {
                  <li><div class="wrk-row"><strong>#{{ a.number }}</strong><span class="wrk-chip" [attr.data-status]="a.outcome === 'complete' ? 'complete' : a.outcome === 'active' ? 'in_progress' : 'closed'">{{ a.outcome }}</span>
                    <wrk-actor [actor]="a.holder" /><span class="wrk-sub">{{ a.started_at | ago }}</span></div>
                    @if (a.reason) { <div class="wrk-sub">{{ a.reason }}</div> }
                    @for (pr of a.prs ?? []; track pr) { <a [href]="pr" target="_blank" rel="noopener noreferrer">{{ pr }}</a> }</li>
                }
              </ul>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-msg">
              <h2 id="wrk-h-msg">Messages on this item <span class="wrk-count">{{ messages().length }}</span></h2>
              <wrk-message-list [messages]="messages()" [project]="it.project_key" [showItem]="false" empty="No messages on this item's thread." (changed)="reload()" />
              <h3 style="margin-top:10px">Post</h3>
              <wrk-message-composer [project]="it.project_key" [item]="it.key" (posted)="reload()" />
            </section>

            @if ((it.knowledge ?? []).length > 0) {
              <section class="wrk-card" aria-labelledby="wrk-h-know">
                <h2 id="wrk-h-know">Relevant knowledge</h2>
                <ul class="wrk-list">@for (k of it.knowledge; track k.id) { <li><div class="wrk-row"><span class="wrk-chip">{{ k.kind }}</span>@if (k.possibly_stale) { <span class="wrk-chip wrk-chip-open">possibly stale</span> }<span>{{ k.summary }}</span></div></li> }</ul>
              </section>
            }
          </div>

          <div style="display:flex;flex-direction:column;gap:14px">
            <section class="wrk-card" aria-labelledby="wrk-h-work">
              <h2 id="wrk-h-work">Work</h2>
              <div class="wrk-form">
                <label>Status
                  <select class="wrk-input" [ngModel]="it.status" (ngModelChange)="setStatus($event)" aria-label="Status">@for (s of statuses; track s) { <option [value]="s">{{ s.replace('_', ' ') }}</option> }</select>
                </label>
                @if (it.claim; as claim) {
                  <div>Claimed by <wrk-actor [actor]="{ name: claim.holder, session_id: claim.session_id, kind: claim.session_id ? 'session' : 'user' }" />, marker {{ claim.marker }}, expires {{ claim.expires_at | ago }}.</div>
                  <div class="wrk-row">
                    <button type="button" class="wrk-btn" [disabled]="busy()" (click)="write({ renew_claim: {} }, 'Claim renewed.')">Renew claim</button>
                    <button type="button" class="wrk-btn" [disabled]="busy()" (click)="write({ release_claim: {} }, 'Claim released.')">Release claim</button>
                  </div>
                } @else {
                  <div class="wrk-sub">Not claimed.</div>
                  <div class="wrk-row"><button type="button" class="wrk-btn" [disabled]="busy()" (click)="write({ claim: { marker: 'ui' } }, 'Claimed.')">Claim</button></div>
                }
              </div>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-com">
              <h2 id="wrk-h-com">Comment</h2>
              <form class="wrk-form" (ngSubmit)="comment()">
                <textarea class="wrk-input" name="comment" rows="3" aria-label="Comment" [(ngModel)]="commentText"></textarea>
                <div class="wrk-row"><button type="submit" class="wrk-btn" [disabled]="busy() || !commentText.trim()">Add comment</button></div>
              </form>
            </section>

            <section class="wrk-card" aria-labelledby="wrk-h-tl">
              <h2 id="wrk-h-tl">Timeline</h2>
              <ol class="wrk-timeline" data-testid="timeline">
                @for (entry of timeline(); track $index) {
                  <li [attr.data-kind]="entry.kind">
                    <div class="wrk-row"><span class="wrk-tl-kind">{{ entry.kind }}</span>@if (entry.actor) { <wrk-actor [actor]="entry.actor" /> }<span class="wrk-sub wrk-grow" style="text-align:right">{{ entry.at | ago }}</span></div>
                    <div>{{ entry.title }}</div>
                    @if (entry.detail) { <div class="wrk-pre wrk-sub">{{ entry.detail }}</div> }
                  </li>
                }
              </ol>
              @if (timeline().length === 0) { <div class="wrk-empty">Nothing recorded yet.</div> }
            </section>
          </div>
        </div>

        @if (dialog() === 'edit') {
          <wrk-dialog heading="Edit item" (closed)="dialog.set('')"><wrk-item-form [initial]="it" (saved)="save($event)" (cancelled)="dialog.set('')" /></wrk-dialog>
        }
      } @else {
        <wrk-error [error]="error()" (reload)="reload()" />
        @if (!error()) { <div class="wrk-empty">Loading…</div> }
      }
    </div>
  `,
  styles: [`.wrk-acquire { margin-top: 10px; } .wrk-acquire summary { cursor: pointer; color: var(--wrk-link); }`]
})
export class WorkItemComponent extends WorkPageBase implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  readonly statuses = WORK_STATUSES;
  readonly item = signal<ItemView | null>(null);
  readonly messages = signal<MessageView[]>([]);
  readonly evidence = signal<GitHubEvidence[]>([]);
  readonly dialog = signal<'' | 'edit'>('');
  readonly met = computed(() => (this.item()?.acceptance_criteria ?? []).filter(criterion => criterion.state === 'met').length);
  readonly timeline = computed(() => {
    const item = this.item();
    return item ? buildTimeline(item, this.feed(), this.messages(), this.evidence()) : [];
  });
  readonly commitsOf = pushCommits;
  readonly short = shortSha;
  commentText = '';
  blocker = '';
  linkKind: WorkLink['kind'] = 'pr';
  linkUrl = '';
  reportSummary = '';
  reportStates: Record<string, string> = {};
  reportEvidence: Record<string, string> = {};
  hDone = '';
  hNext = '';
  hOpen = '';
  hBranch = '';
  hCommit = '';
  private stopStream?: () => void;
  private readonly refresh = withDebounce(() => void this.reload(), 300);

  constructor() {
    super();
  }

  ngOnInit(): void {
    void this.reload().then(() => {
      const item = this.item();
      if (!item) return;
      this.follow(item.project_key);
      this.stopStream = this.live.streamMessages(item.project_key, event => {
        if (!event.message?.item || event.message.item === item.key) this.refresh.call();
      });
    });
  }

  ngOnDestroy(): void {
    this.unfollow();
    this.stopStream?.();
    this.refresh.cancel();
  }

  protected async reload(): Promise<void> {
    const key = this.route.snapshot.paramMap.get('key') ?? '';
    try {
      const item = await this.api.get<ItemView>(key, 'full');
      const messages = await this.api.messages(item.project_key, { item: item.key, limit: 100 });
      const evidence = await this.api.githubEvidence(item.project_key, item.key);
      this.item.set(item);
      this.messages.set(messages.items);
      this.evidence.set(evidence);
      for (const criterion of item.acceptance_criteria ?? []) this.reportStates[criterion.id] ??= criterion.state;
    } catch (error) {
      this.error.set(error instanceof WorkApiError ? error : new WorkApiError(0, 'error', String(error)));
    }
  }

  /** update_work guarded by the version just read; a conflict is shown and the page reloads on request. */
  async write(fields: Record<string, unknown>, success: string): Promise<boolean> {
    const item = this.item()!;
    const result = await this.run(() => this.api.update(item.key, { expected_version: item.version, ...fields }), success);
    if (result) await this.reload();
    return !!result;
  }

  setStatus(status: WorkStatus): Promise<boolean> {
    return this.write({ status }, `Moved to ${status.replace('_', ' ')}.`);
  }

  async save(fields: Record<string, unknown>): Promise<void> {
    if (Object.keys(fields).length === 0) {
      this.dialog.set('');
      return;
    }
    if (await this.write(fields, 'Item saved.')) this.dialog.set('');
  }

  async comment(): Promise<void> {
    if (await this.write({ comment: this.commentText.trim() }, 'Comment added.')) this.commentText = '';
  }

  async addBlocker(): Promise<void> {
    if (await this.write({ add_blocked_by: [this.blocker.trim()] }, 'Dependency added.')) this.blocker = '';
  }

  async addLink(): Promise<void> {
    if (await this.write({ add_links: [{ kind: this.linkKind, url: this.linkUrl.trim() }] }, 'Link added.')) this.linkUrl = '';
  }

  removeLink(link: WorkLink): Promise<boolean> {
    return this.write({ remove_links: [{ kind: link.kind, url: link.url }] }, 'Link removed.');
  }

  async recordHandoff(): Promise<void> {
    const handoff: Record<string, unknown> = {};
    if (this.hDone.trim()) handoff['done'] = this.hDone.trim();
    if (this.hNext.trim()) handoff['next_action'] = this.hNext.trim();
    if (lines(this.hOpen).length > 0) handoff['open_issues'] = lines(this.hOpen);
    if (this.hBranch.trim()) handoff['branch'] = this.hBranch.trim();
    if (this.hCommit.trim()) handoff['commit'] = this.hCommit.trim();
    if (await this.write({ handoff }, 'Handoff recorded.')) this.hDone = this.hNext = this.hOpen = this.hBranch = this.hCommit = '';
  }

  /** The acceptance report; complete sets the status in the same call (only here, by whoever judges it done). */
  async reportAcceptance(complete: boolean): Promise<void> {
    const item = this.item()!;
    const criteria = (item.acceptance_criteria ?? []).map(criterion => ({
      id: criterion.id,
      state: this.reportStates[criterion.id] === 'met' ? 'met' : 'unmet',
      ...(this.reportEvidence[criterion.id]?.trim() ? { evidence: this.reportEvidence[criterion.id].trim() } : {})
    }));
    const report: Record<string, unknown> = { criteria };
    if (this.reportSummary.trim()) report['summary'] = this.reportSummary.trim();
    await this.write({ acceptance_report: report, ...(complete ? { status: 'complete' } : {}) }, complete ? 'Reported and completed.' : 'Acceptance reported.');
  }
}
