import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AdminDatabaseSourcesComponent } from './admin-database-sources.component';

describe('AdminDatabaseSourcesComponent', () => {
  let api: {
    listDatabaseSources: ReturnType<typeof vi.fn>;
    generateDatabaseSourceKey: ReturnType<typeof vi.fn>;
    createDatabaseSource: ReturnType<typeof vi.fn>;
    updateDatabaseSource: ReturnType<typeof vi.fn>;
    deleteDatabaseSource: ReturnType<typeof vi.fn>;
    syncDatabaseSource: ReturnType<typeof vi.fn>;
    syncAllDatabaseSources: ReturnType<typeof vi.fn>;
  };
  let component: AdminDatabaseSourcesComponent;

  const source = {
    id: 7,
    serverName: 'analytics',
    databaseName: 'warehouse',
    connectionString: 'Server=analytics;Password=***',
    enabled: true,
    lastSyncedAt: undefined,
    createdAt: '2026-04-26T00:00:00Z',
    updatedAt: '2026-04-26T00:00:00Z'
  };

  beforeEach(() => {
    api = {
      listDatabaseSources: vi.fn().mockReturnValue(of([source])),
      generateDatabaseSourceKey: vi.fn().mockReturnValue(of({ key: 'abc123' })),
      createDatabaseSource: vi.fn().mockReturnValue(of(source)),
      updateDatabaseSource: vi.fn().mockReturnValue(of({ ...source, enabled: false })),
      deleteDatabaseSource: vi.fn().mockReturnValue(of(void 0)),
      syncDatabaseSource: vi.fn().mockReturnValue(of({ status: 'queued', runId: 42, runStatusUrl: '/api/indexer/runs/42' })),
      syncAllDatabaseSources: vi.fn().mockReturnValue(of({ status: 'queued', runId: 43, runStatusUrl: '/api/indexer/runs/43' }))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AdminDatabaseSourcesComponent());
  });

  it('loads database sources', async () => {
    await component.ngOnInit();

    expect(component.sources()).toEqual([source]);
  });

  it('creates a source with a null database when blank', async () => {
    component.newServerName = 'analytics';
    component.newConnectionString = 'Server=analytics;';

    await component.create();

    expect(api.createDatabaseSource).toHaveBeenCalledWith({
      serverName: 'analytics',
      databaseName: null,
      connectionString: 'Server=analytics;',
      enabled: true
    });
    expect(component.success()).toBe('Added analytics.');
  });

  it('toggles and deletes sources', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));

    await component.toggle(source);
    await component.remove(source);

    expect(api.updateDatabaseSource).toHaveBeenCalledWith(7, { enabled: false });
    expect(api.deleteDatabaseSource).toHaveBeenCalledWith(7);
  });

  it('queues source sync runs', async () => {
    await component.sync(source);
    await component.syncAll();

    expect(api.syncDatabaseSource).toHaveBeenCalledWith(7);
    expect(api.syncAllDatabaseSources).toHaveBeenCalled();
    expect(component.lastRunMessage()).toBe('Run #43');
  });
});
