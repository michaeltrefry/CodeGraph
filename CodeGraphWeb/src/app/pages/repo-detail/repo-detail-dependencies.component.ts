import { Component, Input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LABEL_ICONS, ProjectDetailResponse } from '../../core/models';

@Component({
  selector: 'app-repo-detail-dependencies',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './repo-detail-dependencies.component.html',
  styleUrl: './repo-detail-dependencies.component.scss'
})
export class RepoDetailDependenciesComponent {
  @Input() projectName = '';
  @Input() detail: ProjectDetailResponse | null = null;

  readonly labelIcons = LABEL_ICONS;

  projectRoute(projectName: string) {
    return [projectName.startsWith('db:') ? '/schemas' : '/repos', projectName];
  }

  projectNodesRoute(projectName: string) {
    return [projectName.startsWith('db:') ? '/schemas' : '/repos', projectName, 'nodes'];
  }

  nodeLabelEntries() {
    const detail = this.detail;
    if (!detail) return [];

    return Object.entries(detail.nodeCounts)
      .sort((a, b) => b[1] - a[1])
      .filter(([, count]) => count > 0);
  }
}
