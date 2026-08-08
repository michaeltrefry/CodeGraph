import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from './api.service';
import { ReportingErrorHandler } from './reporting-error-handler';

describe('ReportingErrorHandler', () => {
  afterEach(() => vi.restoreAllMocks());

  it('reports an unhandled error once and keeps the console signal', () => {
    const api = { reportClientError: vi.fn().mockReturnValue(of(undefined)) };
    const injector = Injector.create({ providers: [{ provide: ApiService, useValue: api }] });
    const handler = runInInjectionContext(injector, () => new ReportingErrorHandler());
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const error = new Error('render failed');

    handler.handleError(error);
    handler.handleError(error);

    expect(consoleError).toHaveBeenCalledTimes(2);
    expect(api.reportClientError).toHaveBeenCalledTimes(1);
    expect(api.reportClientError).toHaveBeenCalledWith(expect.objectContaining({
      message: 'render failed',
      stack: expect.stringContaining('render failed')
    }));
  });

  it('handles non-error rejection values', () => {
    const api = { reportClientError: vi.fn().mockReturnValue(of(undefined)) };
    const injector = Injector.create({ providers: [{ provide: ApiService, useValue: api }] });
    const handler = runInInjectionContext(injector, () => new ReportingErrorHandler());
    vi.spyOn(console, 'error').mockImplementation(() => undefined);

    handler.handleError({ rejection: 'promise rejected' });

    expect(api.reportClientError).toHaveBeenCalledWith(expect.objectContaining({
      message: 'promise rejected'
    }));
  });
});
