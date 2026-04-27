import { Component, Input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CONFIDENCE_COLORS, LABEL_ICONS, ProjectDetailResponse, StoredProjectAnalysis } from '../../core/models';

interface RepoProjectEntry {
  projectName: string;
  analysis: StoredProjectAnalysis | null;
}
import { MarkdownComponent } from '../../shared/markdown.component';

@Component({
  selector: 'app-repo-detail-projects',
  standalone: true,
  imports: [RouterLink, MarkdownComponent],
  templateUrl: './repo-detail-projects.component.html',
  styleUrl: './repo-detail-projects.component.scss'
})
export class RepoDetailProjectsComponent {
  @Input() projectName = '';
  @Input() detail: ProjectDetailResponse | null = null;

  expandedAnalysis = signal<string | null>(null);

  readonly labelIcons = LABEL_ICONS;
  readonly confidenceColors = CONFIDENCE_COLORS;

  toggleAnalysis(projectName: string): void {
    this.expandedAnalysis.update(current => current === projectName ? null : projectName);
  }

  projectNodesRoute(projectName: string) {
    return [projectName.startsWith('db:') ? '/schemas' : '/repos', projectName, 'nodes'];
  }

  projectEntries(): RepoProjectEntry[] {
    const detail = this.detail;
    if (!detail) return [];

    const analysesByProject = new Map(
      detail.analyses.map(analysis => [analysis.projectName, analysis] as const)
    );

    const orderedNames = [
      ...detail.analyses.map(analysis => analysis.projectName),
      ...Object.keys(detail.dotnetProjects ?? {})
    ];

    return Array.from(new Set(orderedNames))
      .filter(projectName => !!projectName)
      .map(projectName => ({
        projectName,
        analysis: analysesByProject.get(projectName) ?? null
      }));
  }

  getDotnetProject(projectName: string) {
    const dotnetProjects = this.detail?.dotnetProjects;
    if (!dotnetProjects) return null;

    const counts = dotnetProjects[projectName];
    if (!counts) return null;

    return {
      name: projectName,
      total: Object.values(counts).reduce((sum, count) => sum + count, 0),
      labels: Object.entries(counts).sort((a, b) => b[1] - a[1])
    };
  }
}
