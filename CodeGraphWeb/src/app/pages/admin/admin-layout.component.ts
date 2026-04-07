import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-admin-layout',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="admin-layout">
      <nav class="admin-nav">
        <h2>Settings</h2>
        <a routerLink="/settings/operations" routerLinkActive="active">Operations</a>
        <a routerLink="/settings/sections" routerLinkActive="active">Sections</a>
        <a routerLink="/settings/exclusions" routerLinkActive="active">Exclusions</a>
      </nav>
      <div class="admin-content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [`
    .admin-layout { display: flex; gap: 1.5rem; padding: 1rem; }
    .admin-nav {
      display: flex; flex-direction: column; gap: 0.5rem;
      min-width: 160px; padding: 1rem;
      background: white; border: 1px solid #e5e7eb; border-radius: 8px;
    }
    .admin-nav h2 { margin: 0 0 0.5rem; font-size: 1.1rem; color: #111827; }
    .admin-nav a {
      padding: 0.4rem 0.8rem; border-radius: 4px;
      text-decoration: none; color: #374151;
    }
    .admin-nav a:hover { background: #f3f4f6; }
    .admin-nav a.active { background: #2563eb; color: white; }
    .admin-content { flex: 1; min-width: 0; }
  `]
})
export class AdminLayoutComponent {}
