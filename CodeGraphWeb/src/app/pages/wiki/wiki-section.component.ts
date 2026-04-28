import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { WikiPage, WikiSection, WikiTreeNode } from '../../core/models';
import { MarkdownComponent } from '../../shared/markdown.component';
import { WikiEditorFormComponent } from './wiki-editor-form.component';
import { getWikiSaveErrorMessage, WikiEditorState } from './wiki-editor';
import { SECTION_ROOT_SLUG } from './wiki.constants';

@Component({
  selector: 'app-wiki-section',
  standalone: true,
  imports: [RouterLink, DatePipe, MarkdownComponent, WikiEditorFormComponent],
  template: `
    @if (section()) {
      @if (rootPage()) {
        <header class="wkp-header">
          <nav class="wkp-breadcrumb">
            <a routerLink="/wiki">Wiki</a>
            <span class="sep">/</span>
            <span class="current">{{ section()!.title }}</span>
          </nav>

          <div class="wkp-header-row">
            <div class="wkp-header-main">
              <h1 class="wkp-title">{{ rootPage()!.title }}</h1>
              @if (section()!.description) {
                <p class="wkp-subtitle">{{ section()!.description }}</p>
              }
              <div class="wkp-meta">
                <span class="cg-muted cg-small">
                  Updated by <span class="wkp-meta-strong">{{ rootPage()!.author }}</span> &middot;
                  {{ rootPage()!.updatedAt | date:'medium' }}
                </span>
                <span class="cg-chip cg-chip-mono">v{{ rootPage()!.revision }}</span>
              </div>
            </div>

            @if (section()!.allowUserPages || isAdmin()) {
              <div class="wkp-actions">
                <button class="wkp-btn" type="button" (click)="startEdit()">Edit</button>
                @if (section()!.allowUserPages) {
                  <a class="wkp-btn primary" [routerLink]="['/wiki', section()!.slug, '_new']">New page</a>
                }
              </div>
            }
          </div>
        </header>

        @if (editor.editing()) {
          <app-wiki-editor-form
            [title]="editor.title"
            (titleChange)="editor.title = $event"
            [content]="editor.content"
            (contentChange)="editor.content = $event"
            [rawContent]="editor.rawContent"
            (rawContentChange)="editor.rawContent = $event"
            [hasRawContent]="section()!.hasRawContent"
            [author]="editor.author"
            (authorChange)="editor.author = $event"
            [authorReadonly]="!!currentUsername()"
            [saveError]="editor.saveError()"
            (cancel)="editor.reset()"
            (save)="save()"
          />
        } @else {
          <article class="wkp-article">
            <app-markdown [content]="rootPage()!.content" />
          </article>

          @if (section()!.hasRawContent && rootPage()!.rawContent) {
            <section class="wkp-raw-card">
              <header class="wkp-raw-head">
                <span class="cg-small cg-muted wkp-raw-label">Raw content</span>
              </header>
              <pre class="wkp-raw-body">{{ rootPage()!.rawContent }}</pre>
            </section>
          }
        }
      } @else {
        <header class="wkp-header">
          <nav class="wkp-breadcrumb">
            <a routerLink="/wiki">Wiki</a>
            <span class="sep">/</span>
            <span class="current">{{ section()!.title }}</span>
          </nav>
          <h1 class="wkp-title">{{ section()!.title }}</h1>
          @if (section()!.description) {
            <p class="wkp-subtitle">{{ section()!.description }}</p>
          }
        </header>

        <div class="wkp-empty-root">
          <p class="cg-muted">This section does not have a top-level section page yet.</p>
          @if (section()!.allowUserPages || isAdmin()) {
            <div class="wkp-empty-actions">
              <a class="wkp-btn primary" [routerLink]="['/wiki', section()!.slug, '_new']" [queryParams]="{ root: '1' }">
                Create section page
              </a>
              @if (section()!.allowUserPages) {
                <a class="wkp-btn" [routerLink]="['/wiki', section()!.slug, '_new']">New page</a>
              }
            </div>
          }
        </div>
      }

      @if (pages().length > 0) {
        <section class="wkp-page-list-card">
          <header class="wkp-page-list-head">
            <span class="cg-small cg-muted wkp-raw-label">Pages in this section</span>
            <span class="cg-chip cg-chip-mono">{{ pages().length }}</span>
          </header>
          <ul class="wkp-page-list">
            @for (page of pages(); track page.id) {
              <li>
                <a class="wkp-page-link" [routerLink]="['/wiki', section()!.slug, page.slug]">
                  <span class="wkp-page-title">{{ page.title }}</span>
                  <span class="wkp-page-arrow cg-faint">></span>
                </a>
              </li>
            }
          </ul>
        </section>
      } @else if (!rootPage()) {
        <p class="cg-muted wkp-empty-msg">No pages yet.</p>
      }
    }
  `,
  styles: [`
    :host {
      display: block;
      width: min(100%, 860px);
      max-width: 860px;
      margin: 0 auto;
    }

    .wkp-header { margin-bottom: 28px; }
    .wkp-breadcrumb {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: var(--fs-sm);
      color: var(--muted);
      margin-bottom: 12px;
      flex-wrap: wrap;
    }
    .wkp-breadcrumb a {
      color: var(--muted);
      text-decoration: none;
    }
    .wkp-breadcrumb a:hover { color: var(--text); }
    .wkp-breadcrumb .sep { color: var(--faint); }
    .wkp-breadcrumb .current { color: var(--text); font-weight: 500; }

    .wkp-header-row {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;
      flex-wrap: wrap;
    }
    .wkp-header-main { flex: 1 1 auto; min-width: 0; }

    .wkp-title {
      margin: 0;
      font-size: 34px;
      font-weight: 600;
      color: var(--text);
      line-height: 1.15;
    }
    .wkp-subtitle {
      margin: 8px 0 0;
      color: var(--muted);
      font-size: var(--fs-md);
      line-height: 1.5;
    }
    .wkp-meta {
      display: flex;
      align-items: center;
      gap: 10px;
      margin-top: 12px;
      flex-wrap: wrap;
    }
    .wkp-meta-strong { color: var(--text-2); font-weight: 500; }

    .wkp-actions {
      display: flex;
      gap: 6px;
      flex-shrink: 0;
      flex-wrap: wrap;
    }

    .wkp-btn {
      appearance: none;
      min-height: 34px;
      padding: 6px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface);
      color: var(--text-2);
      font: inherit;
      font-size: var(--fs-sm);
      font-weight: 500;
      cursor: pointer;
      text-decoration: none;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
      white-space: nowrap;
      display: inline-flex;
      align-items: center;
    }
    .wkp-btn:hover {
      background: var(--surface-2);
      color: var(--text);
      border-color: var(--border-2);
      text-decoration: none;
    }
    .wkp-btn.primary {
      background: var(--accent);
      color: white;
      border-color: var(--accent);
    }
    .wkp-btn.primary:hover {
      background: color-mix(in oklab, var(--accent) 85%, black);
      color: white;
    }

    .wkp-article {
      color: var(--text-2);
      line-height: 1.65;
      font-size: 15px;
    }

    .wkp-raw-card {
      margin-top: 28px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      overflow: hidden;
    }
    .wkp-raw-head {
      padding: 10px 16px;
      background: var(--surface-2);
      border-bottom: 1px solid var(--hairline);
    }
    .wkp-raw-label {
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
    }
    .wkp-raw-body {
      margin: 0;
      padding: 14px 16px;
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      line-height: 1.5;
      color: var(--text-2);
      overflow-x: auto;
      white-space: pre-wrap;
      word-wrap: break-word;
    }

    .wkp-empty-root {
      margin-top: 12px;
      padding: 20px 24px;
      border: 1px dashed var(--border-2);
      border-radius: var(--radius-lg);
      background: var(--surface-2);
    }
    .wkp-empty-root p { margin: 0; }
    .wkp-empty-actions {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-top: 14px;
    }

    .wkp-page-list-card {
      margin-top: 32px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      overflow: hidden;
    }
    .wkp-page-list-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 12px 16px;
      border-bottom: 1px solid var(--hairline);
    }
    .wkp-page-list {
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .wkp-page-list li { border-bottom: 1px solid var(--hairline); }
    .wkp-page-list li:last-child { border-bottom: 0; }

    .wkp-page-link {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      padding: 10px 16px;
      text-decoration: none;
      color: var(--text-2);
      transition: background var(--transition), color var(--transition);
    }
    .wkp-page-link:hover {
      background: var(--surface-2);
      color: var(--text);
      text-decoration: none;
    }
    .wkp-page-title {
      font-size: var(--fs-md);
      font-weight: 500;
    }
    .wkp-page-arrow { font-family: var(--font-mono); }
    .wkp-empty-msg { margin-top: 16px; }
  `]
})
export class WikiSectionComponent implements OnInit {
  private api = inject(ApiService);
  private auth = inject(AuthService);
  private route = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);
  private loadRequestId = 0;

  section = signal<WikiSection | null>(null);
  pages = signal<WikiTreeNode[]>([]);
  rootPage = signal<WikiPage | null>(null);
  editor = new WikiEditorState();
  currentUsername = () => this.auth.currentUser()?.username ?? null;
  isAdmin = () => !this.auth.enabled() || this.auth.currentUser()?.isAdmin === true;

  ngOnInit(): void {
    this.route.paramMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => {
        const slug = params.get('section');
        if (!slug) return;
        const requestId = ++this.loadRequestId;

        this.section.set(null);
        this.pages.set([]);
        this.rootPage.set(null);
        this.editor.reset();

        this.api.listSections()
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe(sections => {
            if (requestId !== this.loadRequestId) return;
            this.section.set(sections.find(s => s.slug === slug) ?? null);
          });

        this.api.getSectionTree(slug)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe(tree => {
            if (requestId !== this.loadRequestId) return;
            this.pages.set(tree.filter(page => page.slug !== SECTION_ROOT_SLUG));
          });

        this.api.getWikiPage(slug, SECTION_ROOT_SLUG)
          .pipe(
            catchError(() => of(null)),
            takeUntilDestroyed(this.destroyRef)
          )
          .subscribe(page => {
            if (requestId !== this.loadRequestId) return;
            this.rootPage.set(page);
            this.editor.reset();
          });
      });
  }

  startEdit(): void {
    const page = this.rootPage();
    if (!page) return;

    this.editor.start(page, this.currentUsername());
  }

  save(): void {
    const section = this.section();
    if (!section) return;

    const { request, validationError } = this.editor.buildRequest({
      author: this.currentUsername(),
      hasRawContent: section.hasRawContent,
      trimTitleAndContent: true,
      requireTitleAndContent: true
    });
    if (!request) {
      this.editor.saveError.set(validationError ?? 'Save failed');
      return;
    }

    this.api.updateWikiPage(section.slug, SECTION_ROOT_SLUG, request).subscribe({
      next: () => {
        this.editor.reset();
        this.api.getWikiPage(section.slug, SECTION_ROOT_SLUG).subscribe(page => this.rootPage.set(page));
      },
      error: err => {
        this.editor.saveError.set(getWikiSaveErrorMessage(err));
      }
    });
  }
}
