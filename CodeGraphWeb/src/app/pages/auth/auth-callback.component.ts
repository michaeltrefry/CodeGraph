import { Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `
    <main class="auth-shell">
      <section class="cg-card cg-card-padded status-card">
        <span class="cg-chip cg-chip-accent">Secure sign-in</span>
        @if (!error()) {
          <div class="status-spinner" aria-hidden="true"></div>
        }
        <h1>{{ error() ? 'Sign-in failed' : 'Signing you in' }}</h1>
      @if (error()) {
          <p class="status-error">{{ error() }}</p>
          <button class="cg-btn primary" type="button" (click)="retry()">Try again</button>
      } @else {
          <p class="status-copy">Finishing the OAuth handshake and taking you back into CodeGraph.</p>
      }
      </section>
    </main>
  `,
  styles: [`
    :host {
      display: block;
      min-height: 100%;
      background: var(--bg);
    }

    .auth-shell {
      min-height: 100%;
      display: grid;
      place-items: center;
      padding: 24px;
    }

    .status-card {
      width: min(460px, 100%);
      align-items: center;
      display: grid;
      gap: 16px;
      justify-items: center;
      text-align: center;
    }

    .status-spinner {
      width: 48px;
      height: 48px;
      border-radius: 50%;
      border: 3px solid var(--accent-weak);
      border-top-color: var(--accent);
      animation: spin 0.85s linear infinite;
    }

    h1 {
      margin: 0;
      font-size: var(--fs-h1);
      line-height: 1.15;
      color: var(--text);
    }

    .status-copy,
    .status-error {
      margin: 0;
      color: var(--muted);
      line-height: 1.6;
    }

    .status-error {
      color: var(--sem-red);
    }

    @media (prefers-reduced-motion: reduce) {
      .status-spinner {
        animation: none;
      }
    }

    @keyframes spin {
      to {
        transform: rotate(360deg);
      }
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
