import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-admin-layout',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div class="admin-layout">
      <div class="admin-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    .admin-layout {
      min-height: 100%;
    }

    .admin-content {
      min-width: 0;
    }
  `]
})
export class AdminLayoutComponent {}
