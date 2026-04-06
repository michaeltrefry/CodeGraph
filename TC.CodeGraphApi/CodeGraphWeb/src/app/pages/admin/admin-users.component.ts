import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';

@Component({
  selector: 'app-admin-users',
  standalone: true,
  imports: [FormsModule],
  template: `
    <h2>Admin Users</h2>

    <div class="add-form">
      <input
        type="text"
        [(ngModel)]="newUsername"
        placeholder="Username to add"
        (keyup.enter)="add()"
      />
      <button class="primary" (click)="add()">Add Admin</button>
    </div>

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }

    <ul class="user-list">
      @for (user of admins(); track user) {
        <li>
          <span>{{ user }}</span>
          <button class="danger" (click)="remove(user)">Remove</button>
        </li>
      }
    </ul>
  `,
  styles: [`
    .add-form { display: flex; gap: 0.5rem; margin-bottom: 1rem; }
    input {
      flex: 1; padding: 0.5rem; border-radius: 6px;
      border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827;
    }
    .user-list { list-style: none; padding: 0; }
    .user-list li {
      display: flex; justify-content: space-between; align-items: center;
      padding: 0.5rem 0; border-bottom: 1px solid #e5e7eb;
      color: #111827;
    }
    button {
      padding: 0.4rem 0.8rem; border-radius: 6px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
    button.danger { background: #dc2626; border-color: transparent; color: white; font-size: 0.8rem; }
    button.danger:hover { background: #b91c1c; }
    .error { color: #ef4444; }
  `]
})
export class AdminUsersComponent implements OnInit {
  private api = inject(ApiService);

  admins = signal<string[]>([]);
  newUsername = '';
  error = signal('');

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    try {
      const list = await this.api.listAdminsAsync();
      this.admins.set(list);
    } catch (err: any) {
      this.error.set(err.message || 'Failed to load admins');
    }
  }

  async add(): Promise<void> {
    this.error.set('');
    const username = this.newUsername.trim();
    if (!username) return;

    try {
      await this.api.addAdminAsync(username);
      this.newUsername = '';
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to add admin');
    }
  }

  async remove(username: string): Promise<void> {
    this.error.set('');
    if (!confirm(`Remove admin '${username}'?`)) return;

    try {
      await this.api.removeAdminAsync(username);
      await this.load();
    } catch (err: any) {
      this.error.set(err.message || 'Failed to remove admin');
    }
  }
}
