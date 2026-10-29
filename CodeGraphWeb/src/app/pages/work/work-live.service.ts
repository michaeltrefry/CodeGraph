import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth.service';
import { WorkApiService } from './work-api.service';
import { MessageEvent } from './work.models';

export interface SseFrame { id?: string; event?: string; data: string }

/** Splits a buffer of server-sent events into complete frames and the unfinished rest. */
export function parseSse(buffer: string): { frames: SseFrame[]; rest: string } {
  const frames: SseFrame[] = [];
  const normalized = buffer.replace(/\r\n/g, '\n');
  const blocks = normalized.split('\n\n');
  const rest = blocks.pop() ?? '';
  for (const block of blocks) {
    const frame: SseFrame = { data: '' };
    const data: string[] = [];
    for (const line of block.split('\n')) {
      if (!line || line.startsWith(':')) continue;
      const colon = line.indexOf(':');
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
      if (field === 'id') frame.id = value;
      else if (field === 'event') frame.event = value;
      else if (field === 'data') data.push(value);
    }
    if (data.length > 0) {
      frame.data = data.join('\n');
      frames.push(frame);
    }
  }
  return { frames, rest };
}

const LONG_POLL_SECONDS = 25;

/**
 * Follows the change feed of one project from its head, by long-poll. It keeps no history: pages read
 * what they show (recent changes, an item's changes) with targeted reads and reload when this tells
 * them the feed moved. Listeners are told once when the head is known (so anything written between a
 * page's first read and the head is picked up), then on every batch of new entries.
 */
export class ProjectWatch {
  /** true until the head of the feed is known. */
  readonly loading = signal(true);
  readonly error = signal('');
  private readonly listeners = new Set<() => void>();
  private stopped = false;
  private cursor: string | undefined;

  constructor(private readonly api: WorkApiService, readonly project: string, private readonly pause = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))) {
    void this.run();
  }

  /** Calls back when the feed has news. Returns the unsubscribe. */
  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  stop(): void {
    this.stopped = true;
    this.listeners.clear();
  }

  private notify(): void {
    this.listeners.forEach(listener => listener());
  }

  private async run(): Promise<void> {
    let failures = 0;
    while (!this.stopped) {
      try {
        const result = await this.api.changes(this.project, { cursor: this.cursor, waitSeconds: this.cursor === undefined ? 0 : LONG_POLL_SECONDS, limit: 200 });
        if (this.stopped) return;
        const first = this.cursor === undefined;
        this.cursor = result.next_cursor;
        failures = 0;
        this.error.set('');
        this.loading.set(false);
        if (first || result.items.length > 0) this.notify();
      } catch (error) {
        if (this.stopped) return;
        failures++;
        this.error.set(error instanceof Error ? error.message : String(error));
        this.loading.set(false);
        // The database went back to an earlier state: the cursor is ahead of the feed. Start again at its head.
        if (/ahead of the newest entry|not a cursor of changes/.test(this.error())) {
          this.cursor = undefined;
          continue;
        }
        await this.pause(Math.min(30_000, 2000 * failures));
      }
    }
  }
}

/** A hold on a shared watch; release it when the page goes away. */
export interface WatchLease { readonly watch: ProjectWatch; release(): void }

/** Live updates for the work pages: one shared change-feed watch per project, and the message stream over fetch. */
@Injectable({ providedIn: 'root' })
export class WorkLiveService {
  private readonly api = inject(WorkApiService);
  private readonly auth = inject(AuthService);
  private readonly shared = new Map<string, { watch: ProjectWatch; refs: number; timer?: ReturnType<typeof setTimeout> }>();

  /**
   * The project's shared watch. Pages of one project (and the move from one to the next) share a
   * single long-poll; it stops a few seconds after the last page lets go of it.
   */
  acquire(project: string): WatchLease {
    let entry = this.shared.get(project);
    if (!entry) {
      entry = { watch: new ProjectWatch(this.api, project), refs: 0 };
      this.shared.set(project, entry);
    }
    if (entry.timer) clearTimeout(entry.timer);
    entry.timer = undefined;
    entry.refs++;
    const held = entry;
    let released = false;
    return {
      watch: held.watch,
      release: () => {
        if (released) return;
        released = true;
        held.refs--;
        if (held.refs > 0) return;
        held.timer = setTimeout(() => {
          held.watch.stop();
          if (this.shared.get(project) === held) this.shared.delete(project);
        }, 5000);
      }
    };
  }

  /**
   * Follows the message stream of a project (SSE). The browser's EventSource cannot send the
   * Authorization header the stream needs, so this reads the response body of a fetch. The server
   * starts a stream with no Last-Event-ID at the newest event, so events between a drop and the
   * reconnect are not replayed: onReconnect fires after every connection but the first, and the page
   * reloads once. Last-Event-ID is sent whenever an event has been seen. Returns the function that stops it.
   */
  streamMessages(project: string, onEvent: (event: MessageEvent) => void, onState?: (connected: boolean) => void, onReconnect?: () => void): () => void {
    const controller = new AbortController();
    let lastId: string | undefined;

    const run = async (): Promise<void> => {
      let failures = 0;
      let connections = 0;
      while (!controller.signal.aborted) {
        try {
          const token = await this.auth.getValidAccessToken();
          const headers: Record<string, string> = { Accept: 'text/event-stream' };
          if (token) headers['Authorization'] = `Bearer ${token}`;
          if (lastId) headers['Last-Event-ID'] = lastId;
          const response = await fetch(`${environment.apiUrl}/work/messages/stream?project=${encodeURIComponent(project)}`, { headers, signal: controller.signal });
          if (!response.ok || !response.body) throw new Error(`The message stream answered ${response.status}.`);
          onState?.(true);
          failures = 0;
          if (connections++ > 0) onReconnect?.();
          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            const parsed = parseSse(buffer);
            buffer = parsed.rest;
            for (const frame of parsed.frames) {
              if (frame.id) lastId = frame.id;
              try {
                onEvent(JSON.parse(frame.data) as MessageEvent);
              } catch {
                // A frame that is not an event is skipped.
              }
            }
          }
        } catch {
          if (controller.signal.aborted) return;
        }
        onState?.(false);
        failures++;
        await new Promise(resolve => setTimeout(resolve, Math.min(30_000, 1000 * failures)));
      }
    };

    void run();
    return () => controller.abort();
  }
}

export function withDebounce(fn: () => void, ms = 400): { call: () => void; cancel: () => void } {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return {
    call: () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = undefined;
        fn();
      }, ms);
    },
    cancel: () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
    }
  };
}
