import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { RepoGraphComponent } from '../repos/repo-graph.component';

@Component({
  selector: 'app-graph-page',
  standalone: true,
  imports: [RepoGraphComponent],
  template: `
    <div class="graph-page">
      <div class="graph-page-header">
        <h1>Dependency Graph</h1>
      </div>
      <app-repo-graph class="graph-fill" (nodeClicked)="onNodeClicked($event)" />
    </div>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; flex: 1; min-height: 0; }
    .graph-page {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
      padding: 16px;
      overflow: hidden;
    }
    .graph-page-header {
      margin-bottom: 12px;
      h1 { font-size: 22px; font-weight: 600; margin: 0; }
    }
    .graph-fill {
      display: flex;
      flex-direction: column;
      flex: 1;
      min-height: 0;
    }
  `]
})
export class GraphPageComponent {
  private router = inject(Router);

  onNodeClicked(name: string) {
    this.router.navigate(['/repos', name]);
  }
}
