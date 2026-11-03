import { ProjectView } from '../work/work.models';

export interface ProjectCrumb {
  label: string;
  url: string;
}

/**
 * The breadcrumbs of a /projects URL (its path segments, "projects" first): Projects, then the
 * project's name (its key until the projects are loaded), then "Code reviews" on that page. The
 * tab is not a crumb: the tab bar shows it.
 */
export function projectCrumbs(parts: string[], projects: Pick<ProjectView, 'key' | 'name'>[]): ProjectCrumb[] {
  const crumbs: ProjectCrumb[] = [{ label: 'Projects', url: '/projects' }];
  const key = parts[1] ? decodeURIComponent(parts[1]) : '';
  if (!key) return crumbs;
  const name = projects.find(project => project.key === key)?.name ?? key;
  crumbs.push({ label: name, url: `/projects/${encodeURIComponent(key)}/overview` });
  if (parts[2] === 'code-reviews') crumbs.push({ label: 'Code reviews', url: `/projects/${encodeURIComponent(key)}/code-reviews` });
  return crumbs;
}
