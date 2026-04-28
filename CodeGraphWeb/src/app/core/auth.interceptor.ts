import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!isApiRequest(req.url)) {
    return next(req);
  }

  return from(inject(AuthService).getValidAccessToken()).pipe(
    switchMap(token => {
      if (!token) return next(req);

      return next(req.clone({
        setHeaders: {
          Authorization: `Bearer ${token}`
        }
      }));
    }));
};

function isApiRequest(url: string): boolean {
  if (url.startsWith('/api/')) return true;
  if (url.startsWith(environment.apiUrl)) return true;

  try {
    const parsed = new URL(url);
    const api = new URL(environment.apiUrl, window.location.origin);
    return parsed.origin === api.origin && parsed.pathname.startsWith(api.pathname);
  } catch {
    return false;
  }
}
