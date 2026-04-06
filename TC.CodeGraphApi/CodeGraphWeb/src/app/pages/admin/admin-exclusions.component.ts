import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const API = environment.apiUrl;

interface ExclusionRule {
  id: number;
  targetType: string;
  targetValue: string;
  exclusionType: string;
  reason: string | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}

@Component({
  selector: 'app-admin-exclusions',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <h2>Exclusion Rules</h2>
    <p class="subtitle">Manage which groups or repositories are excluded from indexing and/or AI analysis.</p>

    <div class="add-form">
      <select [(ngModel)]="newTargetType">
        <option value="group">Group</option>
        <option value="repository">Repository</option>
      </select>
      <input type="text" [(ngModel)]="newTargetValue" placeholder="Group name or repo name" />
      <select [(ngModel)]="newExclusionType">
        <option value="complete">Complete (skip entirely)</option>
        <option value="no_analysis">No Analysis (index only)</option>
      </select>
      <input type="text" [(ngModel)]="newReason" placeholder="Reason (optional)" />
      <button class="primary" (click)="create()">Add Rule</button>
    </div>

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
    @if (success()) {
      <p class="success">{{ success() }}</p>
    }

    <table class="rules-table">
      <thead>
        <tr>
          <th>Type</th>
          <th>Target</th>
          <th>Exclusion</th>
          <th>Reason</th>
          <th>Created By</th>
          <th>Created</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        @for (rule of rules(); track rule.id) {
          <tr>
            <td><span class="badge" [class.badge-group]="rule.targetType === 'group'" [class.badge-repo]="rule.targetType === 'repository'">{{ rule.targetType }}</span></td>
            <td><code>{{ rule.targetValue }}</code></td>
            <td><span class="badge" [class.badge-complete]="rule.exclusionType === 'complete'" [class.badge-partial]="rule.exclusionType === 'no_analysis'">{{ rule.exclusionType === 'complete' ? 'Complete' : 'No Analysis' }}</span></td>
            <td>{{ rule.reason || '—' }}</td>
            <td>{{ rule.createdBy }}</td>
            <td>{{ rule.createdAt | date:'short' }}</td>
            <td>
              <button class="danger-sm" (click)="remove(rule)">Delete</button>
            </td>
          </tr>
        }
        @if (rules().length === 0) {
          <tr><td colspan="7" class="muted">No exclusion rules configured.</td></tr>
        }
      </tbody>
    </table>
  `,
  styles: [`
    .subtitle { color: #6b7280; font-size: 0.9rem; margin-bottom: 1rem; }
    .add-form { display: flex; gap: 0.5rem; margin-bottom: 1rem; align-items: center; flex-wrap: wrap; }
    input[type="text"], select {
      padding: 0.4rem; border-radius: 4px;
      border: 1px solid #d1d5db;
      background: #f9fafb; color: #111827;
    }
    input[type="text"] { min-width: 180px; }
    .rules-table { width: 100%; border-collapse: collapse; }
    .rules-table th, .rules-table td {
      padding: 0.5rem; text-align: left; border-bottom: 1px solid #e5e7eb;
      font-size: 0.85rem; color: #111827;
    }
    .rules-table th { font-weight: 600; color: #374151; }
    code { font-size: 0.8rem; background: #f3f4f6; padding: 0.1rem 0.3rem; border-radius: 3px; color: #374151; }
    .badge {
      display: inline-block; padding: 0.15rem 0.4rem; border-radius: 3px;
      font-size: 0.75rem; font-weight: 500;
    }
    .badge-group { background: #dbeafe; color: #1e40af; }
    .badge-repo { background: #fef3c7; color: #92400e; }
    .badge-complete { background: #fee2e2; color: #991b1b; }
    .badge-partial { background: #fef9c3; color: #854d0e; }
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
    .success { color: #10b981; }
    .muted { color: #9ca3af; font-size: 0.85rem; text-align: center; }
  `]
})
export class AdminExclusionsComponent implements OnInit {
  private http = inject(HttpClient);

  rules = signal<ExclusionRule[]>([]);
  newTargetType = 'group';
  newTargetValue = '';
  newExclusionType = 'complete';
  newReason = '';
  error = signal('');
  success = signal('');

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async load(): Promise<void> {
    try {
      const rules = await firstValueFrom(this.http.get<ExclusionRule[]>(`${API}/admin/exclusions`));
      this.rules.set(rules);
    } catch (err: any) {
      this.error.set(err.message || 'Failed to load exclusion rules');
    }
  }

  async create(): Promise<void> {
    this.error.set('');
    this.success.set('');
    if (!this.newTargetValue.trim()) { this.error.set('Target value is required.'); return; }

    try {
      await firstValueFrom(this.http.post(`${API}/admin/exclusions`, {
        targetType: this.newTargetType,
        targetValue: this.newTargetValue.trim(),
        exclusionType: this.newExclusionType,
        reason: this.newReason.trim() || null
      }));
      this.success.set(`Added exclusion for ${this.newTargetType} "${this.newTargetValue.trim()}".`);
      this.newTargetValue = '';
      this.newReason = '';
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to create exclusion rule');
    }
  }

  async remove(rule: ExclusionRule): Promise<void> {
    if (!confirm(`Remove exclusion for ${rule.targetType} "${rule.targetValue}"?`)) return;
    this.error.set('');
    this.success.set('');
    try {
      await firstValueFrom(this.http.delete(`${API}/admin/exclusions/${rule.id}`));
      this.success.set(`Removed exclusion for "${rule.targetValue}".`);
      await this.load();
    } catch (err: any) {
      this.error.set(err.error || err.message || 'Failed to delete exclusion rule');
    }
  }
}
