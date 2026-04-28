import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { WikiSection } from '../../core/models';
import { environment } from '../../../environments/environment';
import { loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

const API = environment.apiUrl;

@Component({
  selector: 'app-admin-sections',
  standalone: true,
  imports: [FormsModule],
  template: `
    <header class="adm-page-header">
      <div>
        <h1>Sections</h1>
        <p>Wiki sections control user-created pages and raw-file content support.</p>
      </div>
    </header>

    <section class="adm-card">
      <header class="adm-card-head">
        <span class="adm-section-label">Add section</span>
      </header>
      <div class="adm-form-row">
        <label class="adm-field wide">
          <span class="adm-field-label">Title</span>
          <input class="adm-input" type="text" [(ngModel)]="newTitle" placeholder="Section title" />
        </label>
        <label class="adm-field narrow">
          <span class="adm-field-label">Icon</span>
          <input class="adm-input" type="text" [(ngModel)]="newIcon" placeholder="Optional" />
        </label>
        <label class="adm-checkbox">
          <input type="checkbox" [(ngModel)]="newAllowUserPages" />
          <span>Allow user pages</span>
        </label>
        <label class="adm-checkbox">
          <input type="checkbox" [(ngModel)]="newHasRawContent" />
          <span>Raw content</span>
        </label>
        <button class="adm-btn primary" type="button" (click)="create()">Create section</button>
      </div>
      @if (error()) {
        <div class="adm-banner err">{{ error() }}</div>
      }
    </section>

    <section class="adm-card adm-card-flush">
      <header class="adm-card-head">
        <span class="adm-section-label">All sections</span>
        <span class="cg-chip cg-chip-mono">{{ sections().length }}</span>
      </header>
      <table class="cg-table">
        <thead>
          <tr>
            <th>Title</th>
            <th>Slug</th>
            <th>Icon</th>
            <th class="cg-cell-num-head">Order</th>
            <th>System</th>
            <th>User pages</th>
            <th>Raw content</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (section of sections(); track section.id) {
            <tr>
              <td>{{ section.title }}</td>
              <td><span class="cg-mono cg-small">{{ section.slug }}</span></td>
              <td>{{ section.icon || '—' }}</td>
              <td class="cg-cell-num">{{ section.sortOrder }}</td>
              <td>
                @if (section.isSystem) {
                  <span class="cg-chip cg-chip-accent">system</span>
                } @else {
                  <span class="cg-faint cg-xsmall">—</span>
                }
              </td>
              <td>
                @if (section.allowUserPages) {
                  <span class="cg-chip cg-chip-ok cg-chip-dot">yes</span>
                } @else {
                  <span class="cg-faint cg-xsmall">no</span>
                }
              </td>
              <td>
                @if (section.hasRawContent) {
                  <span class="cg-chip cg-chip-accent">raw</span>
                } @else {
                  <span class="cg-faint cg-xsmall">—</span>
                }
              </td>
              <td class="cg-cell-actions">
                @if (!section.isSystem) {
                  <button class="adm-btn ghost-danger sm" type="button" (click)="remove(section)">Delete</button>
                } @else {
                  <span class="cg-faint cg-xsmall">Protected</span>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
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
    await loadAdminCollection(
      () => firstValueFrom(this.http.get<WikiSection[]>(`${API}/settings/sections`)),
      this.sections,
      this.error,
      'Failed to load sections'
    );
  }

  async create(): Promise<void> {
    this.error.set('');
    if (!this.newTitle.trim()) { this.error.set('Title is required.'); return; }

    const created = await runAdminMutation(() => firstValueFrom(this.http.post(`${API}/settings/sections`, {
        title: this.newTitle.trim(),
        icon: this.newIcon.trim() || null,
        allowUserPages: this.newAllowUserPages,
        hasRawContent: this.newHasRawContent
      })), {
      error: this.error,
      fallbackError: 'Failed to create section'
    });
    if (!created) return;

    this.newTitle = '';
    this.newIcon = '';
    this.newAllowUserPages = true;
    this.newHasRawContent = false;
    await this.load();
  }

  async remove(section: WikiSection): Promise<void> {
    if (!confirm(`Delete section "${section.title}" and all its pages?`)) return;
    const removed = await runAdminMutation(
      () => firstValueFrom(this.http.delete(`${API}/settings/sections/${section.id}`)),
      { error: this.error, fallbackError: 'Failed to delete section' }
    );
    if (removed) {
      await this.load();
    }
  }
}
