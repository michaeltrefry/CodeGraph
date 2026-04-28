import { signal } from '@angular/core';
import { WikiPage, WikiPageRequest } from '../../core/models';

type WikiEditorSource = Pick<WikiPage, 'title' | 'content' | 'rawContent' | 'author'>;

interface BuildWikiPageRequestOptions {
  author?: string | null;
  hasRawContent: boolean;
  trimTitleAndContent?: boolean;
  requireTitleAndContent?: boolean;
}

interface BuildWikiPageRequestResult {
  request?: WikiPageRequest;
  validationError?: string;
}

export class WikiEditorState {
  editing = signal(false);
  saveError = signal('');

  title = '';
  content = '';
  rawContent = '';
  author = '';

  start(source: WikiEditorSource, author?: string | null): void {
    this.title = source.title;
    this.content = source.content;
    this.rawContent = source.rawContent ?? '';
    this.author = author ?? source.author ?? '';
    this.saveError.set('');
    this.editing.set(true);
  }

  reset(): void {
    this.title = '';
    this.content = '';
    this.rawContent = '';
    this.author = '';
    this.saveError.set('');
    this.editing.set(false);
  }

  buildRequest(options: BuildWikiPageRequestOptions): BuildWikiPageRequestResult {
    const title = options.trimTitleAndContent ? this.title.trim() : this.title;
    const content = options.trimTitleAndContent ? this.content.trim() : this.content;
    const author = (options.author ?? this.author).trim();

    if (options.requireTitleAndContent && (!title || !content)) {
      return { validationError: 'Title and content are required.' };
    }
    if (!author) {
      return { validationError: 'Author name is required.' };
    }

    const request: WikiPageRequest = { title, content, author };
    if (options.hasRawContent) {
      request.rawContent = this.rawContent;
    }

    return { request };
  }
}

export function getWikiSaveErrorMessage(
  error: unknown,
  overrides?: Record<number, string>,
  fallback = 'Save failed'
): string {
  const typedError = error as { status?: number; error?: { message?: string } | string | null } | null;
  const status = typedError?.status;
  if (status != null && overrides?.[status]) {
    return overrides[status];
  }

  const payload = typedError?.error;
  if (typeof payload === 'string' && payload.trim()) {
    return payload;
  }

  if (payload && typeof payload === 'object' && typeof payload.message === 'string' && payload.message.trim()) {
    return payload.message;
  }

  return fallback;
}
