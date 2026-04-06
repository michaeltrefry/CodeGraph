import { Injectable, signal } from '@angular/core';

export interface ChatContext {
  /** Short label shown in the sidebar context bar */
  label: string;
  /** Secondary detail text */
  detail: string;
  /** Context string sent to the API with the question */
  apiContext: string;
}

@Injectable({ providedIn: 'root' })
export class ChatContextService {
  readonly context = signal<ChatContext | null>(null);

  setRepo(repoName: string) {
    this.context.set({
      label: repoName,
      detail: 'Repository',
      apiContext: `The user is viewing repository "${repoName}". Focus answers on this repo.`
    });
  }

  setNode(nodeId: number, nodeName: string, nodeLabel: string, project: string) {
    this.context.set({
      label: `${nodeName}`,
      detail: `${nodeLabel} in ${project}`,
      apiContext: `The user is viewing ${nodeLabel} "${nodeName}" (ID: ${nodeId}) in repository "${project}". Focus answers on this node and its relationships.`
    });
  }

  setNodeList(repoName: string, label?: string) {
    const detail = label ? `${label} nodes` : 'All nodes';
    this.context.set({
      label: repoName,
      detail,
      apiContext: `The user is browsing ${label ?? 'all'} nodes in repository "${repoName}".`
    });
  }

  clear() {
    this.context.set(null);
  }
}
