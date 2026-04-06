import { Component, inject } from '@angular/core';
import { Router, NavigationStart, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { ChatContextService } from './core/chat-context.service';
import { ChatSidebarComponent } from './components/chat-sidebar/chat-sidebar.component';
import { NavSearchComponent } from './components/nav-search/nav-search.component';
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ChatSidebarComponent, NavSearchComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  constructor() {
    const router = inject(Router);
    const chatContext = inject(ChatContextService);

    // Clear context on every navigation; destination page sets its own if applicable
    router.events.pipe(
      filter((e): e is NavigationStart => e instanceof NavigationStart)
    ).subscribe(() => chatContext.clear());
  }
}
