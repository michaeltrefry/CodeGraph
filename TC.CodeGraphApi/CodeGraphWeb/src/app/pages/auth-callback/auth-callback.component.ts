import { Component, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `<div class="callback-container"><p>Signing in...</p></div>`,
  styles: [`.callback-container { display: flex; justify-content: center; align-items: center; height: 50vh; }`]
})
export class AuthCallbackComponent implements OnInit {
  private auth = inject(AuthService);
  private router = inject(Router);

  async ngOnInit(): Promise<void> {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('code');
    const state = params.get('state');
    const error = params.get('error');

    if (error) {
      console.error('Auth error:', error, params.get('error_description'));
      await this.router.navigate(['/']);
      return;
    }

    if (!code || !state) {
      await this.router.navigate(['/']);
      return;
    }

    try {
      const returnUrl = await this.auth.handleCallback(code, state);
      await this.router.navigateByUrl(returnUrl);
    } catch (err) {
      console.error('Token exchange failed:', err);
      await this.router.navigate(['/']);
    }
  }
}
