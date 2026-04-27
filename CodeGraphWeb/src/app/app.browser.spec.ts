import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { App } from './app';

describe('app browser shell', () => {
  it('renders the standalone navigation without wrapping key routes out of view', async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await new Promise(resolve => requestAnimationFrame(() => resolve(undefined)));

    const nativeElement = fixture.nativeElement as HTMLElement;
    const nav = nativeElement.querySelector('.nav') as HTMLElement | null;
    const topbar = nativeElement.querySelector('.topbar') as HTMLElement | null;
    const navLinks = nativeElement.querySelector('.nav-links') as HTMLElement | null;
    const links = Array.from(nativeElement.querySelectorAll<HTMLAnchorElement>('.nav-links > a.nav-link'));

    expect(nav).not.toBeNull();
    expect(topbar).not.toBeNull();
    expect(links.map(link => link.textContent?.trim())).toContain('Repositories');
    expect(links.map(link => link.textContent?.trim())).toEqual([
      'Repositories',
      'Schemas',
      'Graph',
      'Memory',
      'Clusters',
      'Impact',
      'Ask',
      'Wiki',
      'Tokens'
    ]);

    const navRect = nav!.getBoundingClientRect();
    const topbarRect = topbar!.getBoundingClientRect();
    const navLinksRect = navLinks!.getBoundingClientRect();
    expect(navRect.width).toBeGreaterThan(180);
    expect(topbarRect.height).toBeGreaterThan(40);
    expect(topbarRect.height).toBeLessThan(120);
    expect(navLinksRect.left).toBeGreaterThanOrEqual(navRect.left);
    expect(navLinksRect.right).toBeLessThanOrEqual(navRect.right + 1);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth + 1);

    for (const link of links) {
      const rect = link.getBoundingClientRect();
      expect(rect.width).toBeGreaterThan(20);
    }
  });
});
