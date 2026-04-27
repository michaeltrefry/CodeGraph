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
    <div class="page-header">
      <div>
        <h2>Admin Users</h2>
        <p class="subtitle">Manage usernames that can use protected settings and reporting surfaces.</p>
      </div>
    </div>

    <section class="section-card">
      <h3>Add Admin</h3>
      <div class="form-row">
        <input type="text" [(ngModel)]="newUsername" placeholder="username" (keyup.enter)="add()" />
        <button class="primary" type="button" (click)="add()" [disabled]="saving()">Add</button>
      </div>
      @if (error()) {
        <div class="banner error">{{ error() }}</div>
      }
      @if (success()) {
        <div class="banner success">{{ success() }}</div>
      }
    </section>

    <section class="section-card">
      <div class="section-header">
        <h3>Current Admins</h3>
        <span class="count-pill">{{ admins().length }}</span>
      </div>

      @if (admins().length === 0) {
        <p class="empty">No admin users configured.</p>
      } @else {
        <table class="data-table">
          <thead>
            <tr>
              <th>Username</th>
              <th>Added</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (admin of admins(); track admin.username) {
              <tr>
                <td><code>{{ admin.username }}</code></td>
                <td>{{ admin.createdAt | date:'medium' }}</td>
                <td class="actions">
                  <button type="button" class="danger" (click)="remove(admin.username)" [disabled]="saving()">Remove</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .page-header { margin-bottom: 1rem; }
    h2, h3 { margin: 0; color: #111827; }
    .subtitle { margin: 0.35rem 0 0; color: #6b7280; }
    .section-card {
      background: white;
      border: 1px solid #e5e7eb;
      border-radius: 8px;
      padding: 1rem;
      margin-bottom: 1rem;
    }
    .section-header, .form-row {
      display: flex;
      gap: 0.75rem;
      align-items: center;
      justify-content: space-between;
    }
    .form-row { justify-content: flex-start; margin-top: 0.75rem; }
    input {
      width: min(420px, 100%);
      padding: 0.5rem 0.6rem;
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: #f9fafb;
      color: #111827;
    }
    button {
      border: 1px solid #d1d5db;
      border-radius: 6px;
      background: white;
      color: #374151;
      cursor: pointer;
      padding: 0.45rem 0.8rem;
    }
    button.primary { background: #2563eb; border-color: #2563eb; color: white; }
    button.danger { color: #991b1b; border-color: #fecaca; }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    .count-pill {
      border-radius: 999px;
      background: #eff6ff;
      color: #1e40af;
      font-size: 0.8rem;
      font-weight: 700;
      padding: 0.2rem 0.6rem;
    }
    .data-table { width: 100%; border-collapse: collapse; margin-top: 0.75rem; }
    .data-table th, .data-table td {
      border-bottom: 1px solid #e5e7eb;
      padding: 0.6rem;
      text-align: left;
    }
    .data-table th { color: #374151; font-weight: 600; }
    .actions { text-align: right; }
    .empty { color: #6b7280; margin: 0.75rem 0 0; }
    .banner {
      border-radius: 8px;
      margin-top: 0.75rem;
      padding: 0.7rem 0.85rem;
    }
    .banner.error { background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; }
    .banner.success { background: #f0fdf4; border: 1px solid #bbf7d0; color: #166534; }
    code { overflow-wrap: anywhere; }
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
}
