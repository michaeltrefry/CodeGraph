import { Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `
    <div class="auth-callback">
      <h1>{{ error() ? 'Sign-in failed' : 'Signing you in' }}</h1>
      @if (error()) {
        <p>{{ error() }}</p>
        <button type="button" (click)="retry()">Try again</button>
      } @else {
        <p>Finishing the OAuth handshake.</p>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .auth-callback {
      max-width: 420px;
      margin: 12vh auto;
      padding: 1.25rem;
    }
    h1 { margin: 0 0 0.5rem; font-size: 1.35rem; }
    p { color: var(--color-text-muted); }
    button {
      min-height: 36px;
      border: 1px solid var(--color-border-strong);
      border-radius: 6px;
      background: var(--color-surface);
      color: var(--color-text);
      cursor: pointer;
      padding: 0.45rem 0.8rem;
    }
  `]
})
export class AuthCallbackComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly error = signal('');

  async ngOnInit(): Promise<void> {
    try {
      const returnUrl = await this.auth.completeSignIn(window.location.href);
      await this.router.navigateByUrl(returnUrl);
    } catch (err) {
      this.error.set(err instanceof Error ? err.message : 'OAuth callback could not be completed.');
    }
  }

  retry(): void {
    void this.auth.signIn('/');
  }
}
