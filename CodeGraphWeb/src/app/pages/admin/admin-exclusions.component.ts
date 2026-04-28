import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { loadAdminCollection, runAdminMutation } from './admin-resource.helpers';

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
    <header class="adm-page-header">
      <div>
        <h1>Exclusion rules</h1>
        <p>Groups or repositories excluded from indexing and AI analysis.</p>
      </div>
    </header>

    <section class="adm-card">
      <header class="adm-card-head">
        <span class="adm-section-label">Add rule</span>
      </header>
      <div class="adm-form-row">
        <label class="adm-field narrow">
          <span class="adm-field-label">Type</span>
          <select class="adm-select" [(ngModel)]="newTargetType">
            <option value="group">Group</option>
            <option value="repository">Repository</option>
          </select>
        </label>
        <label class="adm-field">
          <span class="adm-field-label">Target</span>
          <input class="adm-input" type="text" [(ngModel)]="newTargetValue" placeholder="Group or repo name" />
        </label>
        <label class="adm-field">
          <span class="adm-field-label">Exclusion</span>
          <select class="adm-select" [(ngModel)]="newExclusionType">
            <option value="complete">Complete (skip entirely)</option>
            <option value="no_analysis">No analysis (index only)</option>
          </select>
        </label>
        <label class="adm-field wide">
          <span class="adm-field-label">Reason</span>
          <input class="adm-input" type="text" [(ngModel)]="newReason" placeholder="Optional" />
        </label>
        <button class="adm-btn primary" type="button" (click)="create()">Add rule</button>
      </div>
      @if (error()) { <div class="adm-banner err">{{ error() }}</div> }
      @if (success()) { <div class="adm-banner ok">{{ success() }}</div> }
    </section>

    <section class="adm-card adm-card-flush">
      <header class="adm-card-head">
        <span class="adm-section-label">Active rules</span>
        <span class="cg-chip cg-chip-mono">{{ rules().length }}</span>
      </header>
      @if (rules().length === 0) {
        <div class="adm-excl-empty cg-muted">No exclusion rules configured.</div>
      } @else {
        <table class="cg-table">
          <thead>
            <tr>
              <th>Type</th>
              <th>Target</th>
              <th>Exclusion</th>
              <th>Reason</th>
              <th>Created by</th>
              <th>Created</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (rule of rules(); track rule.id) {
              <tr>
                <td>
                  <span class="cg-chip"
                        [class.cg-chip-accent]="rule.targetType === 'group'"
                        [class.cg-chip-warn]="rule.targetType === 'repository'">
                    {{ rule.targetType }}
                  </span>
                </td>
                <td><span class="cg-mono cg-small">{{ rule.targetValue }}</span></td>
                <td>
                  <span class="cg-chip"
                        [class.cg-chip-err]="rule.exclusionType === 'complete'"
                        [class.cg-chip-warn]="rule.exclusionType === 'no_analysis'">
                    {{ rule.exclusionType === 'complete' ? 'Complete' : 'No analysis' }}
                  </span>
                </td>
                <td class="cg-small">{{ rule.reason || '—' }}</td>
                <td class="cg-muted cg-small">{{ rule.createdBy }}</td>
                <td class="cg-muted cg-small">{{ rule.createdAt | date:'short' }}</td>
                <td class="cg-cell-actions">
                  <button class="adm-btn ghost-danger sm" type="button" (click)="remove(rule)">Delete</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: 18px; }
    .adm-excl-empty {
      padding: 28px 20px;
      text-align: center;
      font-size: var(--fs-sm);
    }
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
    await loadAdminCollection(
      () => firstValueFrom(this.http.get<ExclusionRule[]>(`${API}/settings/exclusions`)),
      this.rules,
      this.error,
      'Failed to load exclusion rules'
    );
  }

  async create(): Promise<void> {
    this.error.set('');
    this.success.set('');
    if (!this.newTargetValue.trim()) { this.error.set('Target value is required.'); return; }

    const created = await runAdminMutation(() => firstValueFrom(this.http.post(`${API}/settings/exclusions`, {
        targetType: this.newTargetType,
        targetValue: this.newTargetValue.trim(),
        exclusionType: this.newExclusionType,
        reason: this.newReason.trim() || null
      })), {
      error: this.error,
      success: this.success,
      successMessage: `Added exclusion for ${this.newTargetType} "${this.newTargetValue.trim()}".`,
      fallbackError: 'Failed to create exclusion rule'
    });
    if (!created) return;

    this.newTargetValue = '';
    this.newReason = '';
    await this.load();
  }

  async remove(rule: ExclusionRule): Promise<void> {
    if (!confirm(`Remove exclusion for ${rule.targetType} "${rule.targetValue}"?`)) return;
    this.error.set('');
    this.success.set('');
    const removed = await runAdminMutation(
      () => firstValueFrom(this.http.delete(`${API}/settings/exclusions/${rule.id}`)),
      {
        error: this.error,
        success: this.success,
        successMessage: `Removed exclusion for "${rule.targetValue}".`,
        fallbackError: 'Failed to delete exclusion rule'
      }
    );
    if (removed) {
      await this.load();
    }
  }
}
