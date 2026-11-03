import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';

/** Code reviews of a project's repositories: /projects/:key/code-reviews. A placeholder until the page is built. */
@Component({
  selector: 'app-project-code-reviews',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="cg-page">
      <header class="cg-page-header">
        <div>
          <a class="cg-mono cg-small" [routerLink]="['/projects', key(), 'overview']">← {{ key() }}</a>
          <h1>Code reviews</h1>
          <p>AI code review runs across this project's repositories, newest first.</p>
        </div>
      </header>
      <div class="cg-card cg-card-padded"><span class="cg-muted">This page is not built yet.</span></div>
    </div>
  `
})
export class ProjectCodeReviewsComponent {
  readonly key = toSignal(inject(ActivatedRoute).paramMap.pipe(map(params => params.get('key') ?? '')), { initialValue: '' });
}
