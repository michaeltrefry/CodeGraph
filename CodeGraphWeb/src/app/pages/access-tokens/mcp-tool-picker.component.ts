import { Component, computed, input, output } from '@angular/core';
import { McpHubToolResponse } from '../../core/models';

/** Checkbox grid for choosing the exact MCP tools a personal access token can call. */
@Component({
  selector: 'app-mcp-tool-picker',
  standalone: true,
  template: `
    <div class="tool-picker">
      <div class="tool-picker-header">
        <div>
          <h3>{{ heading() }}</h3>
          <p>Select the exact MCP tools this token can call.</p>
        </div>
        <div class="button-row">
          <button type="button" class="cg-btn secondary" (click)="selectReadOnly()">Read-only</button>
          <button type="button" class="cg-btn secondary" (click)="selectAll()">All</button>
        </div>
      </div>
      @if (tools().length === 0) {
        <div class="state-message">No enabled MCP tools are available yet.</div>
      } @else {
        <div class="tool-grid">
          @for (tool of tools(); track tool.toolName) {
            <label class="tool-option" [class.disabled-tool]="!tool.enabled">
              <input
                type="checkbox"
                [checked]="selected().has(tool.toolName)"
                [disabled]="!tool.enabled"
                (change)="toggle(tool.toolName, $any($event.target).checked)" />
              <span>
                <strong>{{ tool.displayName || tool.toolName }}</strong>
                <small>{{ tool.providerKey }} / {{ tool.readOnly ? 'read-only' : 'write' }}</small>
              </span>
            </label>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .tool-picker {
      display: grid;
      gap: 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      padding: 14px;
      background: var(--surface-2);
    }
    .tool-picker-header { display: flex; justify-content: space-between; gap: 12px; align-items: flex-start; }
    .tool-picker-header h3 { margin: 0; color: var(--text); font-size: var(--fs-h4); }
    .tool-picker-header p { margin: 4px 0 0; color: var(--muted); }
    .button-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .tool-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 8px;
      max-height: 280px;
      overflow: auto;
    }
    .tool-option {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr);
      gap: 8px;
      align-items: flex-start;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface);
      padding: 10px;
    }
    .tool-option strong, .tool-option small { display: block; overflow-wrap: anywhere; }
    .tool-option small { color: var(--muted); font-size: var(--fs-xs); }
    .disabled-tool { opacity: 0.55; }
    .state-message { padding: 14px; text-align: center; color: var(--muted); }
    @media (max-width: 720px) { .tool-picker-header { flex-direction: column; } }
  `]
})
export class McpToolPickerComponent {
  readonly tools = input.required<McpHubToolResponse[]>();
  readonly selected = input.required<ReadonlySet<string>>();
  readonly heading = input('Tool access');
  readonly selectedChange = output<Set<string>>();

  private readonly enabledTools = computed(() => this.tools().filter(tool => tool.enabled));

  toggle(toolName: string, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(toolName);
    else next.delete(toolName);
    this.selectedChange.emit(next);
  }

  selectReadOnly(): void {
    this.selectedChange.emit(new Set(this.enabledTools().filter(tool => tool.readOnly).map(tool => tool.toolName)));
  }

  selectAll(): void {
    this.selectedChange.emit(new Set(this.enabledTools().map(tool => tool.toolName)));
  }
}
