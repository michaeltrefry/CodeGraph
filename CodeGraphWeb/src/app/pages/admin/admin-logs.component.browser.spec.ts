import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AdminLogsComponent } from './admin-logs.component';

describe('admin logs browser layout', () => {
  it('renders a filterable, internally scrollable log table with accessible controls', async () => {
    await TestBed.configureTestingModule({
      imports: [AdminLogsComponent],
      providers: [{
        provide: ApiService,
        useValue: {
          getApplicationLogs: () => of({
            entries: [{
              id: 17,
              occurredAtUtc: '2026-08-07T14:00:00Z',
              container: 'memory',
              level: 'Error',
              source: 'CodeGraph.Api@production-host',
              category: 'CodeGraph.Api.Controllers.ClientErrorsController',
              eventId: 42,
              message: 'Unhandled browser error while loading repository analysis',
              exception: 'System.InvalidOperationException: Repository analysis failed\n   at CodeGraph.Api.Controllers.Sample()',
              traceId: '1234567890abcdef1234567890abcdef',
              spanId: '1234567890abcdef',
              propertiesJson: '{"Repository":"CodeGraph"}'
            }],
            page: 1,
            pageSize: 100,
            totalCount: 1,
            totalPages: 1
          })
        }
      }]
    }).compileComponents();

    const fixture = TestBed.createComponent(AdminLogsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await new Promise(resolve => requestAnimationFrame(() => resolve(undefined)));

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent).toContain('Application logs');
    expect(element.querySelectorAll('.filter-panel label')).toHaveLength(5);
    expect(element.querySelector('.log-table tbody tr')).not.toBeNull();
    expect(element.querySelector('.source-cell strong')?.textContent).toContain('Memory');
    expect(element.querySelector('.level-error')?.textContent).toContain('Error');

    const details = element.querySelector('details') as HTMLDetailsElement;
    const summary = details.querySelector('summary') as HTMLElement;
    summary.click();
    expect(details.open).toBe(true);
    expect(element.querySelector('.log-details pre')?.textContent).toContain('InvalidOperationException');

    const tableScroll = element.querySelector('.table-scroll') as HTMLElement;
    expect(tableScroll.scrollWidth).toBeGreaterThanOrEqual(tableScroll.clientWidth);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth + 1);

    for (const button of Array.from(element.querySelectorAll<HTMLButtonElement>('button'))) {
      expect(button.getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
    }
  });
});
