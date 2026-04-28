import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthConfigResponse, CurrentUserResponse } from './models';

interface StoredTokenSet {
  accessToken: string;
  idToken?: string;
  refreshToken?: string;
  tokenType: string;
  expiresAt: number;
  refreshExpiresAt?: number;
}

interface StoredAuthRequest {
  state: string;
  verifier: string;
  returnUrl: string;
  redirectUri: string;
  createdAt: number;
}

interface TokenResponse {
  access_token: string;
  id_token?: string;
  refresh_token?: string;
  token_type?: string;
  expires_in?: number;
  refresh_expires_in?: number;
}

const API = environment.apiUrl;
const TOKEN_KEY = 'codegraph.oauth.tokens';
const REQUEST_KEY = 'codegraph.oauth.request';
const AUTH_REQUEST_TTL_MS = 10 * 60 * 1000;
const ACCESS_TOKEN_EXPIRY_BUFFER_MS = 60_000;
const REFRESH_TIMER_BUFFER_MS = 120_000;
const MIN_REFRESH_DELAY_MS = 15_000;

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private initializePromise?: Promise<void>;
  private refreshPromise?: Promise<string | null>;
  private refreshTimerId?: ReturnType<typeof setTimeout>;

  readonly config = signal<AuthConfigResponse | null>(null);
  readonly currentUser = signal<CurrentUserResponse | null>(null);
  readonly initializing = signal(false);
  readonly authError = signal('');
  readonly enabled = computed(() => this.config()?.enabled ?? false);
  readonly signedIn = computed(() => !this.enabled() || this.hasValidAccessToken());

  initialize(): Promise<void> {
    this.initializePromise ??= this.initializeCore();
    return this.initializePromise;
  }

  getAccessToken(): string | null {
    const tokens = this.readTokens();
    if (!tokens) return null;
    if (this.isAccessTokenUsable(tokens)) {
      return tokens.accessToken;
    }

    if (!this.isRefreshTokenUsable(tokens)) {
      this.clearTokens();
      return null;
    }

    return null;
  }

  async getValidAccessToken(): Promise<string | null> {
    const tokens = this.readTokens();
    if (!tokens) return null;
    if (this.isAccessTokenUsable(tokens)) {
      this.scheduleRefresh(tokens);
      return tokens.accessToken;
    }

    if (!this.isRefreshTokenUsable(tokens)) {
      this.clearTokens();
      return null;
    }

    return this.refreshAccessToken();
  }

  async ensureSignedIn(returnUrl: string): Promise<boolean> {
    await this.initialize();
    if (!this.enabled()) return true;

    if (await this.getValidAccessToken()) {
      await this.loadCurrentUser();
      return true;
    }

    await this.signIn(returnUrl);
    return false;
  }

  async ensureAdmin(returnUrl: string): Promise<boolean> {
    const signedIn = await this.ensureSignedIn(returnUrl);
    if (!signedIn) return false;
    if (!this.enabled() || this.currentUser()?.isAdmin) return true;

    await this.router.navigateByUrl('/repos');
    return false;
  }

  async signIn(returnUrl = this.router.url): Promise<void> {
    await this.initialize();
    const auth = this.config();
    if (!auth?.enabled) return;

    const verifier = this.randomString(64);
    const state = this.randomString(32);
    const redirectUri = this.redirectUri();
    const codeChallenge = await this.pkceChallenge(verifier);
    const request: StoredAuthRequest = {
      state,
      verifier,
      returnUrl: returnUrl || '/',
      redirectUri,
      createdAt: Date.now()
    };
    sessionStorage.setItem(REQUEST_KEY, JSON.stringify(request));

    const params = new URLSearchParams({
      client_id: auth.clientId,
      response_type: 'code',
      redirect_uri: redirectUri,
      scope: auth.scope || 'openid profile email',
      state,
      code_challenge: codeChallenge,
      code_challenge_method: 'S256'
    });

    window.location.assign(`${this.authorizationUrl(auth)}?${params.toString()}`);
  }

  async completeSignIn(callbackUrl: string): Promise<string> {
    await this.initialize();
    const auth = this.requireEnabledConfig();
    const url = new URL(callbackUrl);
    const error = url.searchParams.get('error');
    if (error) {
      const description = url.searchParams.get('error_description');
      throw new Error(description ? `${error}: ${description}` : error);
    }

    const code = url.searchParams.get('code');
    const state = url.searchParams.get('state');
    const request = this.readAuthRequest();
    if (!code || !state || !request || state !== request.state) {
      throw new Error('OAuth callback state could not be verified.');
    }

    sessionStorage.removeItem(REQUEST_KEY);

    const body = new HttpParams()
      .set('grant_type', 'authorization_code')
      .set('client_id', auth.clientId)
      .set('code', code)
      .set('redirect_uri', request.redirectUri)
      .set('code_verifier', request.verifier);

    const token = await firstValueFrom(this.http.post<TokenResponse>(
      this.tokenUrl(auth),
      body.toString(),
      { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }));

    this.storeTokens(token);
    await this.loadCurrentUser();
    return request.returnUrl || '/';
  }

  signOut(): void {
    const tokens = this.readTokens();
    const endSessionUrl = this.config()?.endSessionUrl;
    this.clearTokens();
    this.currentUser.set(null);

    if (this.enabled() && endSessionUrl) {
      const params = new URLSearchParams({
        post_logout_redirect_uri: window.location.origin
      });
      if (tokens?.idToken) {
        params.set('id_token_hint', tokens.idToken);
      }

      window.location.assign(`${endSessionUrl}?${params.toString()}`);
      return;
    }

    void this.router.navigateByUrl('/');
  }

  clearSession(): void {
    this.clearTokens();
    this.currentUser.set(null);
  }

  private async initializeCore(): Promise<void> {
    this.initializing.set(true);
    try {
      const config = await firstValueFrom(this.http.get<AuthConfigResponse>(`${API}/auth/config`));
      this.config.set(config);
      if (config.enabled && await this.getValidAccessToken()) {
        await this.loadCurrentUser();
      }
    } finally {
      this.initializing.set(false);
    }
  }

  private async loadCurrentUser(): Promise<void> {
    if (!this.enabled() || !await this.getValidAccessToken()) return;

    try {
      const user = await firstValueFrom(this.http.get<CurrentUserResponse>(`${API}/auth/me`));
      this.currentUser.set(user);
      this.authError.set('');
    } catch {
      this.clearSession();
    }
  }

  private hasValidAccessToken(): boolean {
    const tokens = this.readTokens();
    return !!tokens && (this.isAccessTokenUsable(tokens) || this.isRefreshTokenUsable(tokens));
  }

  private readTokens(): StoredTokenSet | null {
    try {
      const raw = sessionStorage.getItem(TOKEN_KEY);
      return raw ? JSON.parse(raw) as StoredTokenSet : null;
    } catch {
      return null;
    }
  }

  private async refreshAccessToken(): Promise<string | null> {
    this.refreshPromise ??= this.refreshAccessTokenCore();
    try {
      return await this.refreshPromise;
    } finally {
      this.refreshPromise = undefined;
    }
  }

  private async refreshAccessTokenCore(): Promise<string | null> {
    const tokens = this.readTokens();
    if (!tokens?.refreshToken || !this.isRefreshTokenUsable(tokens)) {
      this.clearSession();
      return null;
    }

    const auth = this.config();
    if (!auth?.enabled) return null;

    try {
      const body = new HttpParams()
        .set('grant_type', 'refresh_token')
        .set('client_id', auth.clientId)
        .set('refresh_token', tokens.refreshToken);

      const response = await firstValueFrom(this.http.post<TokenResponse>(
        this.tokenUrl(auth),
        body.toString(),
        { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }));

      this.storeTokens(response, tokens);
      return this.readTokens()?.accessToken ?? null;
    } catch {
      this.clearSession();
      return null;
    }
  }

  private storeTokens(response: TokenResponse, previous?: StoredTokenSet): void {
    if (!response.access_token) {
      throw new Error('Token response did not include an access token.');
    }

    const expiresIn = Math.max(response.expires_in ?? 3600, 60);
    const refreshExpiresIn = response.refresh_expires_in;
    const tokens: StoredTokenSet = {
      accessToken: response.access_token,
      idToken: response.id_token ?? previous?.idToken,
      refreshToken: response.refresh_token ?? previous?.refreshToken,
      tokenType: response.token_type ?? previous?.tokenType ?? 'Bearer',
      expiresAt: Date.now() + expiresIn * 1000
    };
    if (tokens.refreshToken) {
      tokens.refreshExpiresAt = refreshExpiresIn
        ? Date.now() + Math.max(refreshExpiresIn, 60) * 1000
        : previous?.refreshExpiresAt;
    }
    sessionStorage.setItem(TOKEN_KEY, JSON.stringify(tokens));
    this.scheduleRefresh(tokens);
  }

  private clearTokens(): void {
    this.clearRefreshTimer();
    try {
      sessionStorage.removeItem(TOKEN_KEY);
    } catch {}
  }

  private scheduleRefresh(tokens: StoredTokenSet): void {
    this.clearRefreshTimer();
    if (!tokens.refreshToken || !this.enabled()) return;

    const delay = Math.max(tokens.expiresAt - Date.now() - REFRESH_TIMER_BUFFER_MS, MIN_REFRESH_DELAY_MS);
    this.refreshTimerId = setTimeout(() => {
      void this.refreshAccessToken();
    }, delay);
  }

  private clearRefreshTimer(): void {
    if (!this.refreshTimerId) return;
    clearTimeout(this.refreshTimerId);
    this.refreshTimerId = undefined;
  }

  private isAccessTokenUsable(tokens: StoredTokenSet): boolean {
    return tokens.expiresAt > Date.now() + ACCESS_TOKEN_EXPIRY_BUFFER_MS;
  }

  private isRefreshTokenUsable(tokens: StoredTokenSet): boolean {
    return !!tokens.refreshToken && (!tokens.refreshExpiresAt || tokens.refreshExpiresAt > Date.now() + ACCESS_TOKEN_EXPIRY_BUFFER_MS);
  }

  private readAuthRequest(): StoredAuthRequest | null {
    try {
      const raw = sessionStorage.getItem(REQUEST_KEY);
      if (!raw) return null;
      const request = JSON.parse(raw) as StoredAuthRequest;
      if (Date.now() - request.createdAt > AUTH_REQUEST_TTL_MS) return null;
      return request;
    } catch {
      return null;
    }
  }

  private requireEnabledConfig(): AuthConfigResponse {
    const auth = this.config();
    if (!auth?.enabled) throw new Error('Authentication is not enabled.');
    return auth;
  }

  private authorizationUrl(auth: AuthConfigResponse): string {
    if (auth.authorizationUrl) return auth.authorizationUrl;
    return `${auth.authority.replace(/\/+$/, '')}/connect/authorize`;
  }

  private tokenUrl(auth: AuthConfigResponse): string {
    if (auth.tokenUrl) return auth.tokenUrl;
    return `${auth.authority.replace(/\/+$/, '')}/connect/token`;
  }

  private redirectUri(): string {
    return `${window.location.origin}/auth/callback`;
  }

  private randomString(length: number): string {
    const bytes = new Uint8Array(length);
    crypto.getRandomValues(bytes);
    return this.base64Url(bytes);
  }

  private async pkceChallenge(verifier: string): Promise<string> {
    const data = new TextEncoder().encode(verifier);
    const digest = await crypto.subtle.digest('SHA-256', data);
    return this.base64Url(new Uint8Array(digest));
  }

  private base64Url(bytes: Uint8Array): string {
    let binary = '';
    bytes.forEach(byte => binary += String.fromCharCode(byte));
    return btoa(binary)
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/g, '');
  }
}
