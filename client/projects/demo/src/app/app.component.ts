import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <nav class="topnav">
      <span class="brand">SynForm <small>POC</small></span>
      <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Form</a>
      <a routerLink="/maintainer" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Maintainer</a>
      <a routerLink="/maintainer/lookups" routerLinkActive="on">Lookups</a>
      <a routerLink="/records" routerLinkActive="on">Records</a>
    </nav>
    <main><router-outlet /></main>
  `,
  styles: [`
    :host { display: block; }
    .topnav {
      display: flex; align-items: center; gap: 20px;
      background: #111827; color: #f9fafb; padding: 0 24px; height: 52px;
    }
    .brand { font-weight: 800; margin-right: 16px; }
    .brand small { color: #93c5fd; font-weight: 600; }
    .topnav a {
      color: #d1d5db; text-decoration: none; font-size: 14.5px; padding: 4px 2px;
      border-bottom: 2px solid transparent;
    }
    .topnav a.on { color: #fff; border-bottom-color: #3b82f6; }
    main { padding: 20px 24px; max-width: 1400px; margin: 0 auto; }
  `],
})
export class AppComponent {}
