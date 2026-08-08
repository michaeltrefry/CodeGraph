import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { ApplicationLogPageResponse } from '../../core/models';
import { AdminLogsComponent } from './admin-logs.component';

describe('AdminLogsComponent', () => {
  let api: { getApplicationLogs: ReturnType<typeof vi.fn> };
  let component: AdminLogsComponent;

  const firstPage: ApplicationLogPageResponse = {
    entries: [{
      id: 1,
      occurredAtUtc: '2026-08-07T14:00:00Z',
      container: 'api',
      level: 'Error',
      source: 'CodeGraph.Api@host',
      category: 'CodeGraph.Api.Controllers.ClientErrorsController',
      eventId: 0,
      message: 'Unhandled browser error'
    }],
    page: 1,
    pageSize: 100,
    totalCount: 101,
    totalPages: 2
  };

  beforeEach(() => {
    api = { getApplicationLogs: vi.fn().mockReturnValue(of(firstPage)) };
    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });
    component = runInInjectionContext(injector, () => new AdminLogsComponent());
  });

  it('loads the newest log page with empty filters', async () => {
    await component.ngOnInit();

    expect(api.getApplicationLogs).toHaveBeenCalledWith({
      page: 1,
      container: undefined,
      level: undefined,
      start: undefined,
      end: undefined,
      search: undefined
    });
    expect(component.result()).toBe(firstPage);
    expect(component.resultSummary()).toBe('101 entries');
  });

  it('applies level and text filters from page one', async () => {
    component.page = 2;
    component.container = 'indexer';
    component.level = 'Error';
    component.search = ' timeout ';

    await component.applyFilters();

    expect(component.page).toBe(1);
    expect(api.getApplicationLogs).toHaveBeenLastCalledWith({
      page: 1,
      container: 'indexer',
      level: 'Error',
      start: undefined,
      end: undefined,
      search: 'timeout'
    });
  });

  it('pages within the server-reported bounds', async () => {
    component.result.set(firstPage);

    await component.nextPage();
    await component.nextPage();

    expect(component.page).toBe(2);
    expect(api.getApplicationLogs).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));
  });

  it('formats structured properties and exposes detail availability', () => {
    expect(component.formatProperties('{"trace":"abc"}')).toContain('\n  "trace": "abc"\n');
    expect(component.hasDetails({ ...firstPage.entries[0], traceId: 'abc' })).toBe(true);
    expect(component.containerLabel('metrics')).toBe('Metrics');
    expect(component.levelClass('Critical')).toBe('level-badge level-critical');
  });
});
