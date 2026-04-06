import { Component, inject, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';

@Component({
  selector: 'app-admin-settings',
  standalone: true,
  imports: [FormsModule],
  template: `
    <h2>Settings</h2>
    <p class="hint">Edit CodeGraphServiceSettings overrides (merged over Consul config at runtime). ConnectionString is excluded.</p>

    @if (loading()) {
      <p>Loading...</p>
    } @else {
      <textarea
        class="json-editor"
        [ngModel]="settingsJson()"
        (ngModelChange)="settingsJson.set($event)"
        rows="30"
        spellcheck="false"
      ></textarea>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }
      @if (saved()) {
        <p class="success">Settings saved.</p>
      }

      <div class="actions">
        <button (click)="formatJson()">Format JSON</button>
        <button class="primary" (click)="save()">Save</button>
      </div>
    }
  `,
  styles: [`
    .json-editor {
      width: 100%; font-family: 'Cascadia Code', 'Fira Code', monospace;
      font-size: 0.85rem; padding: 1rem; border-radius: 8px;
      background: #f9fafb; color: #111827;
      border: 1px solid #d1d5db; resize: vertical;
    }
    .hint { color: #6b7280; font-size: 0.85rem; margin-bottom: 1rem; }
    .error { color: #ef4444; }
    .success { color: #16a34a; }
    .actions { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
    button {
      padding: 0.5rem 1rem; border-radius: 6px; border: 1px solid #d1d5db;
      background: white; color: #374151; cursor: pointer;
    }
    button:hover { background: #f3f4f6; }
    button.primary { background: #2563eb; border-color: transparent; color: white; }
    button.primary:hover { background: #1d4ed8; }
  `]
})
export class AdminSettingsComponent implements OnInit {
  private api = inject(ApiService);

  loading = signal(true);
  settingsJson = signal('');
  error = signal('');
  saved = signal(false);

  async ngOnInit(): Promise<void> {
    try {
      const json = await this.api.getAdminSettingsRaw();
      this.settingsJson.set(json);
    } catch (err: any) {
      this.error.set(err.message || 'Failed to load settings');
    } finally {
      this.loading.set(false);
    }
  }

  formatJson(): void {
    try {
      const parsed = JSON.parse(this.settingsJson());
      this.settingsJson.set(JSON.stringify(parsed, null, 2));
      this.error.set('');
    } catch {
      this.error.set('Invalid JSON');
    }
  }

  async save(): Promise<void> {
    this.error.set('');
    this.saved.set(false);

    try {
      JSON.parse(this.settingsJson()); // validate
    } catch {
      this.error.set('Invalid JSON — cannot save.');
      return;
    }

    try {
      await this.api.updateAdminSettings(this.settingsJson());
      this.saved.set(true);
      setTimeout(() => this.saved.set(false), 3000);
    } catch (err: any) {
      this.error.set(err.message || 'Save failed');
    }
  }
}
