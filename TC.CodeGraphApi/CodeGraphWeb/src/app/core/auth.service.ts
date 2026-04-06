import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

const API = environment.apiUrl;

interface AuthConfig {
  authorizationUrl: string;
  tokenUrl: string;
  clientId: string;
  authority: string;
}

interface TokenResponse {
  access_token: string;
  id_token?: string;
  token_type: string;
  expires_in: number;
}

interface IdTokenPayload {
  preferred_username?: string;
  username?: string;
  name?: string;
  sub: string;
  exp: number;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private config: AuthConfig | null = null;

  private _token = signal<string | null>(sessionStorage.getItem('cg_token'));
  private _username = signal<string | null>(sessionStorage.getItem('cg_username'));
  private _isAdmin = signal<boolean>(sessionStorage.getItem('cg_is_admin') === 'true');

  readonly token = this._token.asReadonly();
  readonly username = this._username.asReadonly();
  readonly isAdmin = this._isAdmin.asReadonly();
  readonly isAuthenticated = computed(() => !!this._token());

  async loadConfig(): Promise<AuthConfig> {
    if (this.config) return this.config;
    this.config = await firstValueFrom(this.http.get<AuthConfig>(`${API}/auth/config`));
    return this.config;
  }

  async login(): Promise<void> {
    const config = await this.loadConfig();
    if (!config.authorizationUrl) return;

    const state = this.generateRandom(32);
    const codeVerifier = this.generateRandom(64);
    const codeChallenge = await this.sha256Base64Url(codeVerifier);

    sessionStorage.setItem('cg_state', state);
    sessionStorage.setItem('cg_code_verifier', codeVerifier);
    sessionStorage.setItem('cg_return_url', window.location.pathname);

    const params = new URLSearchParams({
      response_type: 'code',
      client_id: config.clientId,
      redirect_uri: `${window.location.origin}/auth/callback`,
      scope: 'openid username codegraph-api',
      state,
      code_challenge: codeChallenge,
      code_challenge_method: 'S256'
    });

    window.location.href = `${config.authorizationUrl}?${params}`;
  }

  async handleCallback(code: string, state: string): Promise<string> {
    const savedState = sessionStorage.getItem('cg_state');
    if (state !== savedState) throw new Error('State mismatch');

    const config = await this.loadConfig();
    const codeVerifier = sessionStorage.getItem('cg_code_verifier')!;

    const body = new URLSearchParams({
      grant_type: 'authorization_code',
      client_id: config.clientId,
      code,
      redirect_uri: `${window.location.origin}/auth/callback`,
      code_verifier: codeVerifier
    });

    const response = await fetch(config.tokenUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: body.toString()
    });

    if (!response.ok) throw new Error(`Token exchange failed: ${response.status}`);

    const tokenResponse: TokenResponse = await response.json();
    this.setToken(tokenResponse.access_token, tokenResponse.id_token);

    // Clean up PKCE state
    sessionStorage.removeItem('cg_state');
    sessionStorage.removeItem('cg_code_verifier');

    // Check admin status
    await this.checkAdminStatus();

    return sessionStorage.getItem('cg_return_url') || '/';
  }

  logout(): void {
    this._token.set(null);
    this._username.set(null);
    this._isAdmin.set(false);
    sessionStorage.removeItem('cg_token');
    sessionStorage.removeItem('cg_username');
    sessionStorage.removeItem('cg_is_admin');
  }

  getToken(): string | null {
    const token = this._token();
    if (!token) return null;

    // Check expiry
    try {
      const payload = JSON.parse(atob(token.split('.')[1]));
      if (payload.exp && payload.exp * 1000 < Date.now()) {
        this.logout();
        return null;
      }
    } catch {
      return token;
    }
    return token;
  }

  private setToken(accessToken: string, idToken?: string): void {
    this._token.set(accessToken);
    sessionStorage.setItem('cg_token', accessToken);

    // Extract username from id_token or access_token
    const tokenToDecode = idToken || accessToken;
    try {
      const payload: IdTokenPayload = JSON.parse(atob(tokenToDecode.split('.')[1]));
      const username = payload.preferred_username || payload.username || payload.name || payload.sub;
      this._username.set(username);
      sessionStorage.setItem('cg_username', username);
    } catch {
      // If we can't decode, that's ok
    }
  }

  private async checkAdminStatus(): Promise<void> {
    try {
      const admins = await firstValueFrom(
        this.http.get<string[]>(`${API}/admin/admins`, {
          headers: { Authorization: `Bearer ${this._token()}` }
        })
      );
      const isAdmin = admins.includes(this._username()!);
      this._isAdmin.set(isAdmin);
      sessionStorage.setItem('cg_is_admin', String(isAdmin));
    } catch {
      this._isAdmin.set(false);
      sessionStorage.setItem('cg_is_admin', 'false');
    }
  }

  private generateRandom(length: number): string {
    const array = new Uint8Array(length);
    crypto.getRandomValues(array);
    return Array.from(array, b => b.toString(16).padStart(2, '0')).join('');
  }

  private async sha256Base64Url(value: string): Promise<string> {
    const encoder = new TextEncoder();
    const data = encoder.encode(value);
    const hash = await crypto.subtle.digest('SHA-256', data);
    const base64 = btoa(String.fromCharCode(...new Uint8Array(hash)));
    return base64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
}
