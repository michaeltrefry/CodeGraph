import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { WikiSection } from '../../core/models';
import { environment } from '../../../environments/environment';

const API = environment.apiUrl;

@Component({
  selector: 'app-admin-sections',
  standalone: true,
  imports: [FormsModule],
  template: `
    <h2>Section Management</h2>

    <div class="add-form">
      <input type="text" [(ngModel)]="newTitle" placeholder="Section title" />
      <input type="text" [(ngModel)]="newIcon" placeholder="Icon (optional)" />
      <label>
        <input type="checkbox" [(ngModel)]="newAllowUserPages" />
        Allow user pages
      </label>
      <label>
        <input type="checkbox" [(ngModel)]="newHasRawContent" />
        Raw content
      </label>
      <button class="primary" (click)="create()">Create Section</button>
    </div>

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }

    <table class="sections-table">
      <thead>
        <tr>
          <th>Title</th>
          <th>Slug</th>
          <th>Icon</th>
          <th>Order</th>
          <th>System</th>
          <th>User Pages</th>
          <th>Raw Content</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        @for (section of sections(); track section.id) {
          <tr>
            <td>{{ section.title }}</td>
            <td><code>{{ section.slug }}</code></td>
            <td>{{ section.icon || '—' }}</td>
            <td>{{ section.sortOrder }}</td>
            <td>{{ section.isSystem ? 'Yes' : 'No' }}</td>
            <td>{{ section.allowUserPages ? 'Yes' : 'No' }}</td>
            <td>{{ section.hasRawContent ? 'Yes' : 'No' }}</td>
            <td>
              @if (!section.isSystem) {
                <button class="danger-sm" (click)="remove(section)">Delete</button>
              } @else {
                <span class="muted">Protected</span>
              }
            </td>
          </tr>
        }
      </tbody>
    </table>
  `,
  styles: [`
    .add-form { display: flex; gap: 0.5rem; margin-bottom: 1rem; align-items: center; flex-wrap: wrap; }
    input[type="text"] {
      padding: 0.4rem; border-radius: 4px;
      border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827;
    }
    label { font-size: 0.85rem; display: flex; align-items: center; gap: 0.3rem; color: #374151; }
    .sections-table { width: 100%; border-collapse: collapse; }
    .sections-table th, .sections-table td {
      padding: 0.5rem; text-align: left; border-bottom: 1px solid #e5e7eb;
      font-size: 0.85rem; color: #111827;
    }
    .sections-table th { font-weight: 600; color: #374151; }
    code { font-size: 0.8rem; background: #f3f4f6; padding: 0.1rem 0.3rem; border-radius: 3px; color: #374151; }
    button {
      padding: 0.3rem 0.6rem; border-radius: 4px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer; font-size: 0.8rem;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
    button.danger-sm { background: #dc2626; border-color: transparent; color: white; }
    button.danger-sm:hover { background: #b91c1c; }
    .error { color: #ef4444; }
    .muted { color: #9ca3af; font-size: 0.8rem; }
  `]
})
export class AdminSectionsComponent implements OnInit {
  private http = inject(HttpClient);

  sections = signal<WikiSection[]>([]);
  newTitle = '';
  newIcon = '';
  newAllowUserPages = true;
  newHasRawContent = false;
  error = signal('');

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    try {
      const sections = await firstValueFrom(this.http.get<WikiSection[]>(`${API}/admin/sections`));
      this.sections.set(sections);
    } catch (err: any) {
      this.error.set(err.message || 'Failed to load sections');
    }
  }

  async create(): Promise<void> {
    this.error.set('');
    if (!this.newTitle.trim()) { this.error.set('Title is required.'); return; }

    try {
      await firstValueFrom(this.http.post(`${API}/admin/sections`, {
        title: this.newTitle.trim(),
        icon: this.newIcon.trim() || null,
        allowUserPages: this.newAllowUserPages,
        hasRawContent: this.newHasRawContent
      }));
      this.newTitle = '';
      this.newIcon = '';
      this.newAllowUserPages = true;
      this.newHasRawContent = false;
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to create section');
    }
  }

  async remove(section: WikiSection): Promise<void> {
    if (!confirm(`Delete section "${section.title}" and all its pages?`)) return;
    try {
      await firstValueFrom(this.http.delete(`${API}/admin/sections/${section.id}`));
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to delete section');
    }
  }
}
