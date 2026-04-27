import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { ChatContextService } from '../../core/chat-context.service';
import { GraphNode, StoredProjectAnalysis, LABEL_ICONS, CONFIDENCE_COLORS } from '../../core/models';

/** Labels that act as containers for members */
const CONTAINER_LABELS = new Set([
  'Class', 'Interface', 'Enum', 'Struct', 'Record', 'Component', 'Module'
]);

/** Labels that are members of a container */
const MEMBER_LABELS = new Set([
  'Method', 'Property', 'Constructor', 'Delegate', 'Function'
]);

/** Display order for label sections */
const SECTION_ORDER: string[] = [
  'Repository', 'DotnetProject',
  'Class', 'Interface', 'Enum', 'Struct', 'Record',
  'Component', 'Module',
  'Route', 'Service', 'Event', 'Queue', 'Exchange', 'Job',
  'Table', 'View', 'StoredProcedure',
  'Function', 'Method', 'Property', 'Constructor', 'Delegate',
  'File', 'Namespace', 'Folder', 'NuGetPackage', 'Project'
];

export interface ContainerNode {
  node: GraphNode;
  members: GraphNode[];
}

export interface NodeSection {
  label: string;
  icon: string;
  containers: ContainerNode[];   // for container labels with nested members
  standalone: GraphNode[];       // nodes not parented under a container
  totalCount: number;
}

@Component({
  selector: 'app-node-list',
  imports: [RouterLink],
  templateUrl: './node-list.component.html',
  styleUrl: './node-list.component.scss'
})
export class NodeListComponent implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  private chatContext = inject(ChatContextService);

  projectName = signal('');
  label = signal('');
  dotnetProject = signal('');
  allNodes = signal<GraphNode[]>([]);
  total = signal(0);
  loading = signal(true);
  analysis = signal<StoredProjectAnalysis | null>(null);

  /** Which sections are expanded (by label name) */
  expandedSections = signal<Set<string>>(new Set());
  /** Which containers are expanded (by node id) */
  expandedContainers = signal<Set<number>>(new Set());

  readonly labelIcons: Record<string, string> = LABEL_ICONS;
  readonly confidenceColors = CONFIDENCE_COLORS;

  sections = computed<NodeSection[]>(() => {
    const nodes = this.allNodes();
    if (nodes.length === 0) return [];

    // If filtered to a single label, skip sectioning - just show flat or container view
    const filterLabel = this.label();

    // Build container map: qualifiedName -> ContainerNode
    const containerMap = new Map<string, ContainerNode>();
    const containerNodes: GraphNode[] = [];
    const memberNodes: GraphNode[] = [];
    const otherNodes: GraphNode[] = [];

    for (const node of nodes) {
      if (CONTAINER_LABELS.has(node.label)) {
        containerNodes.push(node);
        containerMap.set(node.qualifiedName, { node, members: [] });
      } else if (MEMBER_LABELS.has(node.label)) {
        memberNodes.push(node);
      } else {
        otherNodes.push(node);
      }
    }

    // Assign members to their parent container by qualifiedName prefix
    const orphanMembers: GraphNode[] = [];
    for (const member of memberNodes) {
      let assigned = false;
      // Try to find parent: strip the last segment of qualifiedName
      const lastDot = member.qualifiedName.lastIndexOf('.');
      if (lastDot > 0) {
        const parentQN = member.qualifiedName.substring(0, lastDot);
        const container = containerMap.get(parentQN);
        if (container) {
          container.members.push(member);
          assigned = true;
        }
      }
      if (!assigned) {
        orphanMembers.push(member);
      }
    }

    // Sort members within each container
    for (const container of containerMap.values()) {
      container.members.sort((a, b) => this.memberSortKey(a).localeCompare(this.memberSortKey(b)));
    }

    // If filtering to a single label that's a container type, show just those containers
    if (filterLabel && CONTAINER_LABELS.has(filterLabel)) {
      const containers = containerNodes
        .filter(n => n.label === filterLabel)
        .sort((a, b) => a.name.localeCompare(b.name))
        .map(n => containerMap.get(n.qualifiedName)!);
      return [{
        label: filterLabel,
        icon: this.labelIcons[filterLabel] || '•',
        containers,
        standalone: [],
        totalCount: containers.reduce((sum, c) => sum + 1 + c.members.length, 0)
      }];
    }

    // If filtering to a single member label, show orphans only (members without container parent shown flat)
    if (filterLabel && MEMBER_LABELS.has(filterLabel)) {
      const filtered = [...memberNodes].sort((a, b) => a.name.localeCompare(b.name));
      return [{
        label: filterLabel,
        icon: this.labelIcons[filterLabel] || '•',
        containers: [],
        standalone: filtered,
        totalCount: filtered.length
      }];
    }

    // If filtering to a single non-container/non-member label
    if (filterLabel) {
      const filtered = otherNodes
        .filter(n => n.label === filterLabel)
        .sort((a, b) => a.name.localeCompare(b.name));
      return [{
        label: filterLabel,
        icon: this.labelIcons[filterLabel] || '•',
        containers: [],
        standalone: filtered,
        totalCount: filtered.length
      }];
    }

    // No filter: build sections by label
    const sectionMap = new Map<string, NodeSection>();

    // Container sections
    for (const node of containerNodes) {
      let section = sectionMap.get(node.label);
      if (!section) {
        section = {
          label: node.label,
          icon: this.labelIcons[node.label] || '•',
          containers: [],
          standalone: [],
          totalCount: 0
        };
        sectionMap.set(node.label, section);
      }
      const container = containerMap.get(node.qualifiedName)!;
      section.containers.push(container);
      section.totalCount += 1 + container.members.length;
    }

    // Orphan members go into their own label sections
    for (const node of orphanMembers) {
      let section = sectionMap.get(node.label);
      if (!section) {
        section = {
          label: node.label,
          icon: this.labelIcons[node.label] || '•',
          containers: [],
          standalone: [],
          totalCount: 0
        };
        sectionMap.set(node.label, section);
      }
      section.standalone.push(node);
      section.totalCount++;
    }

    // Other nodes
    for (const node of otherNodes) {
      let section = sectionMap.get(node.label);
      if (!section) {
        section = {
          label: node.label,
          icon: this.labelIcons[node.label] || '•',
          containers: [],
          standalone: [],
          totalCount: 0
        };
        sectionMap.set(node.label, section);
      }
      section.standalone.push(node);
      section.totalCount++;
    }

    // Sort containers and standalone within each section
    for (const section of sectionMap.values()) {
      section.containers.sort((a, b) => a.node.name.localeCompare(b.node.name));
      section.standalone.sort((a, b) => a.name.localeCompare(b.name));
    }

    // Return sections in display order
    const ordered: NodeSection[] = [];
    for (const label of SECTION_ORDER) {
      const section = sectionMap.get(label);
      if (section) ordered.push(section);
    }
    // Any labels not in SECTION_ORDER go at the end
    for (const [label, section] of sectionMap) {
      if (!SECTION_ORDER.includes(label)) ordered.push(section);
    }

    return ordered;
  });

  ngOnInit() {
    const name = this.route.snapshot.paramMap.get('name') ?? '';
    const label = this.route.snapshot.queryParamMap.get('label') ?? '';
    const dotnetProject = this.route.snapshot.queryParamMap.get('dotnetProject') ?? '';
    this.projectName.set(name);
    this.label.set(label);
    this.dotnetProject.set(dotnetProject);
    this.chatContext.setNodeList(name, label || undefined);
    this.load();
  }

  load() {
    this.loading.set(true);
    // Load all nodes at once for tree building
    this.api.getProjectNodes(
      this.projectName(),
      this.label() || undefined,
      this.dotnetProject() || undefined,
      1,
      10000
    ).subscribe({
      next: r => {
        this.allNodes.set(r.items);
        this.total.set(r.total);
        // Auto-expand all sections when filtered to a single label, or if few sections
        const sections = this.sections();
        if (this.label() || sections.length <= 3) {
          this.expandedSections.set(new Set(sections.map(s => s.label)));
        }
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });

    // Load project detail to get matching analysis
    this.api.getProject(this.projectName()).subscribe({
      next: d => {
        const dp = this.dotnetProject();
        if (dp) {
          // Find analysis matching the dotnet project
          const match = d.analyses.find(a =>
            a.projectName.toLowerCase() === dp.toLowerCase());
          this.analysis.set(match ?? null);
        } else if (d.summary) {
          // No dotnet project filter — use repo-level summary as a synthetic analysis
          this.analysis.set({
            repo: d.project.name,
            projectName: d.project.name,
            summary: d.summary.summary,
            confidence: d.summary.confidence,
            endpoints: [],
            services: [],
            externalDependencies: [],
            databaseTables: [],
            modelUsed: d.summary.modelUsed,
            updatedAt: d.summary.updatedAt
          });
        }
      }
    });
  }

  icon() {
    return this.labelIcons[this.label()] ?? '•';
  }

  projectBaseRoute(): string {
    return this.projectName().startsWith('db:') ? '/schemas' : '/repos';
  }

  projectBaseLabel(): string {
    return this.projectName().startsWith('db:') ? 'Schemas' : 'Repositories';
  }

  toggleSection(label: string) {
    this.expandedSections.update(set => {
      const next = new Set(set);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  }

  isSectionExpanded(label: string): boolean {
    return this.expandedSections().has(label);
  }

  toggleContainer(id: number) {
    this.expandedContainers.update(set => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  isContainerExpanded(id: number): boolean {
    return this.expandedContainers().has(id);
  }

  expandAllInSection(section: NodeSection, event: Event) {
    event.stopPropagation();
    const allExpanded = section.containers.every(c => this.isContainerExpanded(c.node.id));
    this.expandedContainers.update(set => {
      const next = new Set(set);
      for (const c of section.containers) {
        if (allExpanded) next.delete(c.node.id);
        else next.add(c.node.id);
      }
      return next;
    });
  }

  hasProperties(node: GraphNode) {
    return node.properties && Object.keys(node.properties).length > 0;
  }

  topProp(node: GraphNode): string {
    const entries = Object.entries(node.properties ?? {});
    const interesting = entries.find(([k]) =>
      ['returnType', 'httpMethod', 'route', 'signature', 'queueName', 'lifetime', 'interfaceName'].includes(k));
    if (interesting) return `${interesting[0]}: ${interesting[1]}`;
    return entries[0] ? `${entries[0][0]}: ${entries[0][1]}` : '';
  }

  pluralize(label: string): string {
    if (label.endsWith('s')) return label + 'es';       // Class -> Classes
    if (label.endsWith('y')) return label.slice(0, -1) + 'ies'; // Property -> Properties
    return label + 's';
  }

  memberIcon(label: string): string {
    return this.labelIcons[label] ?? '•';
  }

  private memberSortKey(node: GraphNode): string {
    // Sort constructors first, then by label, then by name
    const labelOrder = node.label === 'Constructor' ? '0' : node.label === 'Property' ? '1' : '2';
    return `${labelOrder}_${node.name}`;
  }
}
