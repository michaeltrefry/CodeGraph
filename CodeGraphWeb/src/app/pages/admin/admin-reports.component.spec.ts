import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AdminReportResponse } from '../../core/models';
import { AdminReportsComponent } from './admin-reports.component';

describe('AdminReportsComponent', () => {
  let api: {
    getAdminReport: ReturnType<typeof vi.fn>;
    getAdminReportFilters: ReturnType<typeof vi.fn>;
  };
  let component: AdminReportsComponent;

  const report: AdminReportResponse = {
    range: { start: '2026-04-01T00:00:00Z', end: '2026-04-26T00:00:00Z' },
    interval: 'day',
    totals: [{ key: 'requests', label: 'Requests', value: 4 }],
    series: [{ key: 'requests', label: 'Requests', points: [{ bucketStart: '2026-04-26T00:00:00Z', value: 4 }] }],
    breakdowns: [{ dimension: 'provider', key: 'anthropic', label: 'Anthropic', value: 4 }],
    appliedFilters: {}
  };

  beforeEach(() => {
    api = {
      getAdminReport: vi.fn().mockReturnValue(of(report)),
      getAdminReportFilters: vi.fn().mockReturnValue(of({
        users: ['michael'],
        providers: ['anthropic'],
        models: ['claude'],
        tools: ['search_memory']
      }))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AdminReportsComponent());
  });

  it('loads the selected report and filters', async () => {
    await component.ngOnInit();

    expect(api.getAdminReport).toHaveBeenCalledWith('assistant/usage', { interval: 'day', user: undefined, provider: undefined });
    expect(component.report()).toBe(report);
    expect(component.filters()?.providers).toEqual(['anthropic']);
  });

  it('passes user and provider filters to the API', async () => {
    component.user = 'michael';
    component.provider = 'anthropic';

    await component.load();

    expect(api.getAdminReport).toHaveBeenCalledWith('assistant/usage', {
      interval: 'day',
      user: 'michael',
      provider: 'anthropic'
    });
  });

  it('scales bar heights against the largest point', () => {
    expect(component.barHeight(5, [{ value: 10 }, { value: 5 }])).toBe(50);
    expect(component.barHeight(0, [{ value: 0 }])).toBe(4);
  });
});
