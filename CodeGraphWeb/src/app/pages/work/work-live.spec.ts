import { describe, expect, it, vi } from 'vitest';
import { ProjectWatch, parseSse, withDebounce } from './work-live.service';
import { WorkApiService } from './work-api.service';
import { WorkChange } from './work.models';

const change = (sequence: number): WorkChange => ({
  sequence,
  project_id: 'p',
  project_key: 'W',
  entity: 'item',
  entity_id: 'i',
  entity_key: 'W-1',
  kind: 'item_updated',
  actor: { name: 'michael', kind: 'user' },
  changes: [],
  created_at: '2026-01-01T00:00:00Z'
});

describe('parseSse', () => {
  it('reads complete frames and keeps the unfinished rest', () => {
    const { frames, rest } = parseSse('id: 4\nevent: message\ndata: {"a":1}\n\n: keepalive\n\nid: 5\nevent: mess');
    expect(frames).toEqual([{ id: '4', event: 'message', data: '{"a":1}' }]);
    expect(rest).toBe('id: 5\nevent: mess');
  });

  it('joins multi-line data and tolerates CRLF', () => {
    const { frames } = parseSse('data: one\r\ndata: two\r\n\r\n');
    expect(frames[0].data).toBe('one\ntwo');
  });
});

describe('ProjectWatch', () => {
  it('reads the history, then follows new entries and tells listeners', async () => {
    const calls: unknown[] = [];
    const pages = [
      { items: [change(1), change(2)], next_cursor: 'c2' },
      { items: [change(3)], next_cursor: 'c3' }
    ];
    let watch!: ProjectWatch;
    const api = {
      changes: vi.fn(async (_project: string, options: unknown) => {
        calls.push(options);
        const page = pages.shift();
        if (page) return page;
        watch.stop();
        return { items: [], next_cursor: 'c3' };
      })
    } as unknown as WorkApiService;
    watch = new ProjectWatch(api, 'W', async () => undefined);
    const heard = vi.fn();
    watch.onChange(heard);

    await vi.waitFor(() => expect(watch.entries()).toHaveLength(3));
    expect(watch.loading()).toBe(false);
    expect(calls[0]).toMatchObject({ fromStart: true, waitSeconds: 0 });
    expect(calls[1]).toMatchObject({ cursor: 'c2', waitSeconds: 25 });
    expect(heard).toHaveBeenCalledTimes(1);
    watch.stop();
  });

  it('reports a failure and keeps trying', async () => {
    let watch!: ProjectWatch;
    let attempts = 0;
    const api = {
      changes: vi.fn(async () => {
        attempts++;
        if (attempts === 1) throw new Error('offline');
        watch.stop();
        return { items: [], next_cursor: 'c' };
      })
    } as unknown as WorkApiService;
    watch = new ProjectWatch(api, 'W', async () => undefined);
    await vi.waitFor(() => expect(attempts).toBe(2));
  });
});

describe('withDebounce', () => {
  it('runs once after the last call', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const debounced = withDebounce(fn, 100);
    debounced.call();
    debounced.call();
    vi.advanceTimersByTime(99);
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(2);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });
});
