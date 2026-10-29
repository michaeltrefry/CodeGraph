import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LABEL_ICONS, RepositoryDetailResponse } from '../../core/models';

@Component({
  selector: 'app-repo-detail-dependencies',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './repo-detail-dependencies.component.html',
  styleUrl: './repo-detail-dependencies.component.scss'
})
export class RepoDetailDependenciesComponent {
  @Input() repositoryName = '';
  @Input() detail: RepositoryDetailResponse | null = null;

  readonly labelIcons = LABEL_ICONS;

  repositoryRoute(repositoryName: string) {
    return [repositoryName.startsWith('db:') ? '/schemas' : '/repos', repositoryName];
  }

  repositoryNodesRoute(repositoryName: string) {
    return [repositoryName.startsWith('db:') ? '/schemas' : '/repos', repositoryName, 'nodes'];
  }

  nodeLabelEntries() {
    const detail = this.detail;
    if (!detail) return [];

    return Object.entries(detail.nodeCounts)
      .sort((a, b) => b[1] - a[1])
      .filter(([, count]) => count > 0);
  }
}
