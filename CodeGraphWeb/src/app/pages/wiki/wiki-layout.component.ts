import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-wiki-layout',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div class="wk-layout">
      <div class="wk-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      height: 100%;
    }

    .wk-layout {
      display: flex;
      min-height: 100%;
      background: var(--bg);
    }

    .wk-content {
      flex: 1;
      min-width: 0;
      padding: 32px 40px 48px;
      background: var(--bg);
    }

    @media (max-width: 720px) {
      .wk-content { padding: 24px 20px 40px; }
    }
  `]
})
export class WikiLayoutComponent {}
