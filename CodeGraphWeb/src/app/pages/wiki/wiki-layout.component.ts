import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-wiki-layout',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div class="wiki-layout">
      <div class="wiki-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    .wiki-layout {
      min-height: 100%;
    }

    .wiki-content {
      min-width: 0;
      padding: 1.5rem 2rem 3rem;
    }
  `]
})
export class WikiLayoutComponent {}
