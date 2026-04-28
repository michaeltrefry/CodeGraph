import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AdminUserResponse } from '../../core/models';
import { extractAdminError, loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

@Component({
  selector: 'app-admin-users',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Admin users</h1>
        <p>Usernames granted access to protected settings and reporting surfaces.</p>
      </div>
    </header>

    <section class="adm-card">
      <header class="adm-card-head">
        <span class="adm-section-label">Add admin</span>
      </header>
      <div class="adm-form-row">
        <label class="adm-field wide">
          <span class="adm-field-label">Username</span>
          <input class="adm-input" type="text" [(ngModel)]="newUsername" placeholder="Username to grant admin access" (keyup.enter)="add()" />
        </label>
        <button class="adm-btn primary" type="button" (click)="add()" [disabled]="saving()">Add admin</button>
      </div>
      @if (error()) {
        <div class="adm-banner err">{{ error() }}</div>
      }
      @if (success()) {
        <div class="adm-banner ok">{{ success() }}</div>
      }
    </section>

    <section class="adm-card adm-card-flush">
      <header class="adm-card-head">
        <span class="adm-section-label">Current admins</span>
        <span class="cg-chip cg-chip-mono">{{ admins().length }}</span>
      </header>

      @if (admins().length === 0) {
        <div class="adm-users-empty cg-muted">No admin users configured.</div>
      } @else {
        <ul class="adm-user-list">
          @for (admin of admins(); track admin.username) {
            <li class="adm-user-row">
              <div class="adm-user-info">
                <div class="adm-user-avatar">{{ userInitials(admin.username) }}</div>
                <span class="adm-user-name cg-mono">{{ admin.username }}</span>
                <span class="cg-chip cg-chip-accent">admin</span>
                <span class="cg-muted cg-small">Added {{ admin.createdAt | date:'medium' }}</span>
              </div>
              <button class="adm-btn ghost-danger sm" type="button" (click)="remove(admin.username)" [disabled]="saving()">Remove</button>
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }

    .adm-user-list {
      list-style: none;
      padding: 0;
      margin: 0;
    }

    .adm-user-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 10px 20px;
      border-bottom: 1px solid var(--hairline);
    }
    .adm-user-row:last-child { border-bottom: 0; }

    .adm-user-info {
      display: flex;
      align-items: center;
      gap: 10px;
      min-width: 0;
      flex: 1;
      flex-wrap: wrap;
    }

    .adm-user-avatar {
      width: 28px;
      height: 28px;
      border-radius: 50%;
      background: linear-gradient(135deg, var(--sem-purple), var(--accent));
      display: grid;
      place-items: center;
      font-size: var(--fs-xs);
      font-weight: 700;
      color: white;
      flex: 0 0 auto;
    }

    .adm-user-name {
      font-size: var(--fs-md);
      color: var(--text);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      min-width: 0;
    }

    .adm-users-empty {
      padding: 28px 20px;
      text-align: center;
      font-size: var(--fs-sm);
    }
  `]
})
export class AdminUsersComponent implements OnInit {
  private api = inject(ApiService);

  admins = signal<AdminUserResponse[]>([]);
  error = signal('');
  success = signal('');
  saving = signal(false);
  newUsername = '';

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    await loadAdminCollection(
      () => firstValueFrom(this.api.listAdmins()),
      this.admins,
      this.error,
      'Failed to load admin users');
  }

  async add(): Promise<void> {
    const username = this.newUsername.trim();
    this.error.set('');
    this.success.set('');

    if (!username) {
      this.error.set('Username is required.');
      return;
    }

    this.saving.set(true);
    try {
      const added = await runAdminMutation(
        () => firstValueFrom(this.api.addAdmin(username)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Added ${username}.`,
          fallbackError: 'Failed to add admin user'
        });
      if (added) {
        this.newUsername = '';
        await this.load();
      }
    } finally {
      this.saving.set(false);
    }
  }

  async remove(username: string): Promise<void> {
    if (!confirm(`Remove admin '${username}'?`)) return;

    this.saving.set(true);
    try {
      const removed = await runAdminMutation(
        () => firstValueFrom(this.api.removeAdmin(username)),
        {
          error: this.error,
          success: this.success,
          successMessage: `Removed ${username}.`,
          fallbackError: 'Failed to remove admin user'
        });
      if (removed) await this.load();
    } catch (err) {
      this.error.set(extractAdminError(err, 'Failed to remove admin user'));
    } finally {
      this.saving.set(false);
    }
  }

  userInitials(username: string): string {
    const parts = username.split(/[\s._@-]+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return username.slice(0, 2).toUpperCase();
  }
}
