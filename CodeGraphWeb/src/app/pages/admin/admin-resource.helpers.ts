import { WritableSignal } from '@angular/core';

export function extractAdminError(error: unknown, fallback: string): string {
  const typed = error as { error?: { message?: string } | string | null; message?: string } | null;
  const payload = typed?.error;

  if (typeof payload === 'string' && payload.trim()) return payload;
  if (payload && typeof payload === 'object' && typeof payload.message === 'string' && payload.message.trim()) {
    return payload.message;
  }
  if (typeof typed?.message === 'string' && typed.message.trim()) return typed.message;

  return fallback;
}

export async function loadAdminCollection<T>(
  load: () => Promise<T>,
  target: WritableSignal<T>,
  error: WritableSignal<string>,
  fallback = 'Failed to load data'
): Promise<void> {
  error.set('');
  try {
    target.set(await load());
  } catch (err) {
    error.set(extractAdminError(err, fallback));
  }
}

export async function runAdminMutation(
  action: () => Promise<unknown>,
  options: {
    error: WritableSignal<string>;
    success?: WritableSignal<string>;
    successMessage?: string;
    fallbackError: string;
  }
): Promise<boolean> {
  options.error.set('');
  options.success?.set('');

  try {
    await action();
    if (options.success && options.successMessage) {
      options.success.set(options.successMessage);
    }
    return true;
  } catch (err) {
    options.error.set(extractAdminError(err, options.fallbackError));
    return false;
  }
}
