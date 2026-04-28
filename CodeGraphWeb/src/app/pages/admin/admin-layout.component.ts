import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-admin-layout',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div class="adm-layout">
      <div class="adm-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      height: 100%;
      min-height: 0;
    }

    .adm-layout {
      display: flex;
      height: 100%;
      min-height: 0;
      background: var(--bg);
    }

    .adm-content {
      flex: 1;
      min-width: 0;
      min-height: 0;
      overflow-y: auto;
      padding: 32px 40px 48px;
      background: var(--bg);
    }

    @media (max-width: 760px) {
      .adm-content {
        padding: 20px 16px 32px;
      }
    }
  `]
})
export class AdminLayoutComponent {}
