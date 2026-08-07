import { ErrorHandler, Injectable, inject } from '@angular/core';
import { finalize } from 'rxjs';
import { ApiService } from './api.service';

@Injectable()
export class ReportingErrorHandler implements ErrorHandler {
  private readonly api = inject(ApiService);
  private reporting = false;
  private lastSignature = '';
  private lastReportedAt = 0;

  handleError(error: unknown): void {
    console.error(error);

    const normalized = this.normalize(error);
    const url = this.currentPageUrl();
    const signature = `${url}|${normalized.message}|${normalized.stack ?? ''}`;
    const now = Date.now();
    if (this.reporting || (signature === this.lastSignature && now - this.lastReportedAt < 10_000)) {
      return;
    }

    this.reporting = true;
    this.lastSignature = signature;
    this.lastReportedAt = now;
    this.api.reportClientError({
      message: normalized.message.slice(0, 4_096),
      stack: normalized.stack?.slice(0, 32_768),
      url: url.slice(0, 2_048),
      userAgent: typeof navigator === 'undefined' ? undefined : navigator.userAgent.slice(0, 512)
    }).pipe(
      finalize(() => this.reporting = false)
    ).subscribe({
      error: () => {
        // The error reporting path must not create another unhandled error.
      }
    });
  }

  private normalize(error: unknown): { message: string; stack?: string } {
    const candidate = this.unwrap(error);
    if (candidate instanceof Error) {
      return {
        message: candidate.message || candidate.name || 'Unhandled browser error',
        stack: candidate.stack
      };
    }

    if (typeof candidate === 'string') {
      return { message: candidate };
    }

    if (candidate && typeof candidate === 'object' && 'message' in candidate) {
      const value = candidate as { message?: unknown; stack?: unknown };
      return {
        message: String(value.message ?? 'Unhandled browser error'),
        stack: typeof value.stack === 'string' ? value.stack : undefined
      };
    }

    try {
      return { message: JSON.stringify(candidate) || 'Unhandled browser error' };
    } catch {
      return { message: String(candidate || 'Unhandled browser error') };
    }
  }

  private unwrap(error: unknown): unknown {
    if (error && typeof error === 'object') {
      const value = error as { rejection?: unknown; error?: unknown };
      return value.rejection ?? value.error ?? error;
    }
    return error;
  }

  private currentPageUrl(): string {
    if (typeof window === 'undefined') return '';
    return `${window.location.origin}${window.location.pathname}`;
  }
}
