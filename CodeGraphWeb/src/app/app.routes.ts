import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'repos', pathMatch: 'full' },
  {
    path: 'repos',
    loadComponent: () => import('./pages/repos/repos.component').then(m => m.ReposComponent)
  },
  {
    path: 'repos/:name',
    loadComponent: () => import('./pages/repo-detail/repo-detail.component').then(m => m.RepoDetailComponent)
  },
  {
    path: 'repos/:name/nodes',
    loadComponent: () => import('./pages/node-list/node-list.component').then(m => m.NodeListComponent)
  },
  {
    path: 'nodes/:id',
    loadComponent: () => import('./pages/node-detail/node-detail.component').then(m => m.NodeDetailComponent)
  },
  {
    path: 'search',
    loadComponent: () => import('./pages/search/search.component').then(m => m.SearchComponent)
  },
  {
    path: 'graph',
    loadComponent: () => import('./pages/graph/graph-page.component').then(m => m.GraphPageComponent)
  },
  {
    path: 'memory',
    loadComponent: () => import('./pages/memory/memory-page.component').then(m => m.MemoryPageComponent)
  },
  {
    path: 'clusters',
    loadComponent: () => import('./pages/clusters/clusters-page.component').then(m => m.ClustersPageComponent)
  },
  {
    path: 'impact',
    loadComponent: () => import('./pages/impact/impact.component').then(m => m.ImpactComponent)
  },
  {
    path: 'ask',
    loadComponent: () => import('./pages/ask/ask.component').then(m => m.AskComponent)
  },
  // Wiki (replaces old /conventions routes)
  {
    path: 'wiki',
    loadComponent: () => import('./pages/wiki/wiki-layout.component').then(m => m.WikiLayoutComponent),
    children: [
      { path: '', redirectTo: 'general', pathMatch: 'full' },
      {
        path: ':section',
        loadComponent: () => import('./pages/wiki/wiki-section.component').then(m => m.WikiSectionComponent)
      },
      {
        path: ':section/_new',
        loadComponent: () => import('./pages/wiki/wiki-page-new.component').then(m => m.WikiPageNewComponent)
      },
      {
        path: ':section/:path1',
        loadComponent: () => import('./pages/wiki/wiki-page.component').then(m => m.WikiPageComponent)
      },
      {
        path: ':section/:path1/_new',
        loadComponent: () => import('./pages/wiki/wiki-page-new.component').then(m => m.WikiPageNewComponent)
      },
      {
        path: ':section/:path1/:path2',
        loadComponent: () => import('./pages/wiki/wiki-page.component').then(m => m.WikiPageComponent)
      },
      {
        path: ':section/:path1/:path2/_new',
        loadComponent: () => import('./pages/wiki/wiki-page-new.component').then(m => m.WikiPageNewComponent)
      },
      {
        path: ':section/:path1/:path2/:path3/_new',
        loadComponent: () => import('./pages/wiki/wiki-page-new.component').then(m => m.WikiPageNewComponent)
      },
      {
        path: ':section/:path1/:path2/:path3',
        loadComponent: () => import('./pages/wiki/wiki-page.component').then(m => m.WikiPageComponent)
      },
      {
        path: ':section/:path1/:path2/:path3/:path4',
        loadComponent: () => import('./pages/wiki/wiki-page.component').then(m => m.WikiPageComponent)
      }
    ]
  },
  // Redirect old convention URLs
  { path: 'conventions', redirectTo: 'wiki/conventions', pathMatch: 'full' },
  { path: 'conventions/:slug', redirectTo: 'wiki/conventions/:slug' },
  // Settings
  {
    path: 'settings',
    loadComponent: () => import('./pages/admin/admin-layout.component').then(m => m.AdminLayoutComponent),
    children: [
      { path: '', redirectTo: 'operations', pathMatch: 'full' },
      {
        path: 'operations',
        loadComponent: () => import('./pages/admin/admin-operations.component').then(m => m.AdminOperationsComponent)
      },
      {
        path: 'schedules',
        loadComponent: () => import('./pages/admin/admin-schedules.component').then(m => m.AdminSchedulesComponent)
      },
      {
        path: 'db-health',
        loadComponent: () => import('./pages/admin/admin-db-health.component').then(m => m.AdminDbHealthComponent)
      },
      {
        path: 'sections',
        loadComponent: () => import('./pages/admin/admin-sections.component').then(m => m.AdminSectionsComponent)
      },
      {
        path: 'exclusions',
        loadComponent: () => import('./pages/admin/admin-exclusions.component').then(m => m.AdminExclusionsComponent)
      }
    ]
  },
  { path: 'admin', redirectTo: 'settings', pathMatch: 'full' },
  { path: 'admin/:path', redirectTo: 'settings/:path' }
];
