import { CanActivateChildFn, CanActivateFn } from '@angular/router';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = async (_route, state) =>
  inject(AuthService).ensureSignedIn(state.url);

export const authChildGuard: CanActivateChildFn = async (_route, state) =>
  inject(AuthService).ensureSignedIn(state.url);

export const adminGuard: CanActivateFn = async (_route, state) =>
  inject(AuthService).ensureAdmin(state.url);
