import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { extractAdminError, loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

describe('admin resource helpers', () => {
  it('prefers API error payloads over generic messages', () => {
    expect(extractAdminError({ error: { message: 'Nested' }, message: 'Outer' }, 'fallback')).toBe('Nested');
    expect(extractAdminError({ error: 'Payload' }, 'fallback')).toBe('Payload');
    expect(extractAdminError({}, 'fallback')).toBe('fallback');
  });

  it('loads collections into signals and records failures', async () => {
    const target = signal<number[]>([]);
    const error = signal('');

    await loadAdminCollection(async () => [1, 2, 3], target, error, 'failed');
    expect(target()).toEqual([1, 2, 3]);
    expect(error()).toBe('');

    await loadAdminCollection(async () => {
      throw new Error('boom');
    }, target, error, 'failed');

    expect(error()).toBe('boom');
  });

  it('runs mutations with shared success and error handling', async () => {
    const error = signal('');
    const success = signal('');

    const ok = await runAdminMutation(async () => {}, {
      error,
      success,
      successMessage: 'Saved',
      fallbackError: 'failed'
    });

    expect(ok).toBe(true);
    expect(success()).toBe('Saved');

    const failed = await runAdminMutation(async () => {
      throw { error: { message: 'Nope' } };
    }, {
      error,
      success,
      successMessage: 'Saved',
      fallbackError: 'failed'
    });

    expect(failed).toBe(false);
    expect(error()).toBe('Nope');
  });
});
