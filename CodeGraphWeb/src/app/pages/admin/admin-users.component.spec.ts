import { Injector, runInInjectionContext } from '@angular/core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiService } from '../../core/api.service';
import { AdminUsersComponent } from './admin-users.component';

describe('AdminUsersComponent', () => {
  let api: {
    listAdmins: ReturnType<typeof vi.fn>;
    addAdmin: ReturnType<typeof vi.fn>;
    removeAdmin: ReturnType<typeof vi.fn>;
  };
  let component: AdminUsersComponent;

  beforeEach(() => {
    api = {
      listAdmins: vi.fn().mockReturnValue(of([{ username: 'michael', createdAt: '2026-04-26T00:00:00Z' }])),
      addAdmin: vi.fn().mockReturnValue(of({ username: 'alex', createdAt: '2026-04-26T00:00:00Z' })),
      removeAdmin: vi.fn().mockReturnValue(of(void 0))
    };

    const injector = Injector.create({
      providers: [{ provide: ApiService, useValue: api }]
    });

    component = runInInjectionContext(injector, () => new AdminUsersComponent());
  });

  it('loads admin users', async () => {
    await component.ngOnInit();

    expect(component.admins()).toEqual([{ username: 'michael', createdAt: '2026-04-26T00:00:00Z' }]);
    expect(component.error()).toBe('');
  });

  it('adds a trimmed admin user and reloads the list', async () => {
    component.newUsername = ' alex ';

    await component.add();

    expect(api.addAdmin).toHaveBeenCalledWith('alex');
    expect(api.listAdmins).toHaveBeenCalledTimes(1);
    expect(component.newUsername).toBe('');
    expect(component.success()).toBe('Added alex.');
  });

  it('removes an admin after confirmation', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));

    await component.remove('michael');

    expect(api.removeAdmin).toHaveBeenCalledWith('michael');
  });
});
