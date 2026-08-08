import { Injector, runInInjectionContext, signal } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { SchemaListItem, SchemaListResponse } from '../../core/models';
import { SchemasComponent } from './schemas.component';

describe('SchemasComponent', () => {
  let api: {
    listSchemas: ReturnType<typeof vi.fn>;
    deleteProject: ReturnType<typeof vi.fn>;
  };
  let auth: {
    enabled: ReturnType<typeof signal<boolean>>;
    currentUser: ReturnType<typeof signal<{ username: string; isAdmin: boolean } | null>>;
  };
  let component: SchemasComponent;

  const emptySchema: SchemaListItem = {
    name: 'db:CodeGraph-MariaDb:codegraph',
    serverName: 'CodeGraph-MariaDb',
    databaseName: 'codegraph',
    tableCount: 0,
    viewCount: 0,
    procedureCount: 0,
    framework: 'MariaDB',
    language: 'SQL',
    properties: {}
  };
  const populatedSchema: SchemaListItem = {
    ...emptySchema,
    name: 'db:Trefry.net:codegraph',
    serverName: 'Trefry.net',
    tableCount: 73
  };

  const response = (items: SchemaListItem[]): SchemaListResponse => ({
    items,
    total: items.length,
    totalTables: items.reduce((sum, item) => sum + item.tableCount, 0),
    totalViews: items.reduce((sum, item) => sum + item.viewCount, 0),
    totalProcedures: items.reduce((sum, item) => sum + item.procedureCount, 0),
    page: 1,
    pageSize: 25,
    servers: [...new Set(items.map(item => item.serverName))],
    databases: [...new Set(items.map(item => item.databaseName))]
  });

  beforeEach(() => {
    api = {
      listSchemas: vi.fn().mockReturnValue(of(response([emptySchema, populatedSchema]))),
      deleteProject: vi.fn().mockReturnValue(of(void 0))
    };
    auth = {
      enabled: signal(true),
      currentUser: signal<{ username: string; isAdmin: boolean } | null>({ username: 'admin', isAdmin: true })
    };

    const injector = Injector.create({
      providers: [
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: Router, useValue: { navigate: vi.fn() } }
      ]
    });

    component = runInInjectionContext(injector, () => new SchemasComponent());
  });

  it('exposes deletion only to admins', () => {
    expect(component.isAdmin()).toBe(true);

    auth.currentUser.set({ username: 'viewer', isAdmin: false });

    expect(component.isAdmin()).toBe(false);
    component.deleteSchema(emptySchema);
    expect(api.deleteProject).not.toHaveBeenCalled();
  });

  it('confirms the exact schema, deletes it, and refreshes the list', () => {
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    api.listSchemas.mockReturnValue(of(response([populatedSchema])));
    component.items.set([emptySchema, populatedSchema]);

    component.deleteSchema(emptySchema);

    expect(confirm).toHaveBeenCalledWith(
      "Delete indexed schema 'db:CodeGraph-MariaDb:codegraph' from CodeGraph?\n\n" +
      "This removes only CodeGraph's indexed graph data. It does not delete the source database."
    );
    expect(api.deleteProject).toHaveBeenCalledWith('db:CodeGraph-MariaDb:codegraph');
    expect(component.items()).toEqual([populatedSchema]);
    expect(component.notice()).toBe("Deleted indexed schema 'db:CodeGraph-MariaDb:codegraph'.");
    expect(component.deletingSchema()).toBeNull();
  });

  it('keeps the schema visible and reports a failed deletion', () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    api.deleteProject.mockReturnValue(throwError(() => new Error('failed')));
    component.items.set([emptySchema, populatedSchema]);

    component.deleteSchema(emptySchema);

    expect(component.items()).toEqual([emptySchema, populatedSchema]);
    expect(component.error()).toBe("Unable to delete indexed schema 'db:CodeGraph-MariaDb:codegraph'.");
    expect(component.deletingSchema()).toBeNull();
  });
});
