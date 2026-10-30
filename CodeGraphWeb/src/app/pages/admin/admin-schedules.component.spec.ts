import { HttpClient } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminSchedulesComponent } from './admin-schedules.component';

describe('AdminSchedulesComponent retention schedule', () => {
  let http: { get: ReturnType<typeof vi.fn>; post: ReturnType<typeof vi.fn>; put: ReturnType<typeof vi.fn> };
  let component: AdminSchedulesComponent;
  beforeEach(() => {
    http = { get: vi.fn(() => of([])), post: vi.fn(() => of({})), put: vi.fn(() => of({})) };
    const injector = Injector.create({ providers: [{ provide: HttpClient, useValue: http }] });
    component = runInInjectionContext(injector, () => new AdminSchedulesComponent());
    component.jobType = 'IndexerRunRetention';
    component.name = 'indexer-run-retention';
  });
  it('offers the retention type and creates with the default 30 day argument', async () => {
    expect(component.jobTypes.some(type => type.value === 'IndexerRunRetention')).toBe(true);
    await component.save();
    expect(http.post).toHaveBeenCalledWith(expect.stringContaining('/settings/schedules'), expect.objectContaining({ jobType: 'IndexerRunRetention', args: { retentionDays: 30 } }));
  });
  it('round-trips an edited retention value and resets the default', async () => {
    component.edit({ id: 9, name: 'retention', jobType: 'IndexerRunRetention', isEnabled: true, cronExpression: '0 4 * * 0', timeZoneId: 'UTC', args: { retentionDays: 45 }, nextRunUtc: '', lastRunStartedUtc: null, lastRunCompletedUtc: null, lastRunStatus: null, lastError: null, isRunning: false });
    expect(component.retentionDays).toBe(45);
    component.retentionDays = 60;
    await component.save();
    expect(http.put).toHaveBeenCalledWith(expect.stringContaining('/settings/schedules/9'), expect.objectContaining({ args: { retentionDays: 60 } }));
    expect(component.retentionDays).toBe(30);
  });
  it.each([0, -1, 1.5, NaN, Infinity])('rejects invalid retentionDays %s before sending', async value => {
    component.retentionDays = value;
    await component.save();
    expect(http.post).not.toHaveBeenCalled();
    expect(component.error()).toContain('positive whole number');
    expect(component.saving()).toBe(false);
  });
});
