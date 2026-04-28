import { HttpClient } from '@angular/common/http';
import { Injector, runInInjectionContext } from '@angular/core';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthConfigResponse } from './models';
import { AuthService } from './auth.service';

const TOKEN_KEY = 'codegraph.oauth.tokens';

describe('AuthService token renewal', () => {
  let http: { get: ReturnType<typeof vi.fn>; post: ReturnType<typeof vi.fn> };
  let router: { navigateByUrl: ReturnType<typeof vi.fn>; url: string };
  let service: AuthService;

  const authConfig: AuthConfigResponse = {
    enabled: true,
    authority: 'https://auth.example.test/realms/codegraph',
    authorizationUrl: 'https://auth.example.test/authorize',
    tokenUrl: 'https://auth.example.test/token',
    endSessionUrl: 'https://auth.example.test/logout',
    clientId: 'codegraph-web',
    audience: 'codegraph-api',
    scope: 'openid profile email'
  };

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-04-28T12:00:00Z'));
    sessionStorage.clear();

    http = {
      get: vi.fn(),
      post: vi.fn()
    };
    router = {
      navigateByUrl: vi.fn(),
      url: '/settings/llm'
    };

    const injector = Injector.create({
      providers: [
        AuthService,
        { provide: HttpClient, useValue: http },
        { provide: Router, useValue: router }
      ]
    });

    service = runInInjectionContext(injector, () => injector.get(AuthService));
    service.config.set(authConfig);
  });

  afterEach(() => {
    service.clearSession();
    vi.useRealTimers();
    sessionStorage.clear();
  });

  it('refreshes an expired access token before returning one to API callers', async () => {
    sessionStorage.setItem(TOKEN_KEY, JSON.stringify({
      accessToken: 'expired-access',
      refreshToken: 'refresh-token',
      tokenType: 'Bearer',
      expiresAt: Date.now() - 1000,
      refreshExpiresAt: Date.now() + 10 * 60 * 1000
    }));
    http.post.mockReturnValue(of({
      access_token: 'fresh-access',
      refresh_token: 'fresh-refresh',
      token_type: 'Bearer',
      expires_in: 3600,
      refresh_expires_in: 7200
    }));

    const token = await service.getValidAccessToken();

    expect(token).toBe('fresh-access');
    expect(http.post).toHaveBeenCalledWith(
      'https://auth.example.test/token',
      expect.stringContaining('grant_type=refresh_token'),
      expect.objectContaining({
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' }
      }));
    expect(http.post.mock.calls[0][1]).toContain('refresh_token=refresh-token');

    const stored = JSON.parse(sessionStorage.getItem(TOKEN_KEY) ?? '{}');
    expect(stored.accessToken).toBe('fresh-access');
    expect(stored.refreshToken).toBe('fresh-refresh');
  });

  it('clears the session when refresh fails instead of keeping a stale token', async () => {
    sessionStorage.setItem(TOKEN_KEY, JSON.stringify({
      accessToken: 'expired-access',
      refreshToken: 'refresh-token',
      tokenType: 'Bearer',
      expiresAt: Date.now() - 1000,
      refreshExpiresAt: Date.now() + 10 * 60 * 1000
    }));
    http.post.mockReturnValue(throwError(() => new Error('invalid_grant')));

    await expect(service.getValidAccessToken()).resolves.toBeNull();

    expect(sessionStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(service.currentUser()).toBeNull();
  });
});
