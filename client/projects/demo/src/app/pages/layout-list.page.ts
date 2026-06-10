import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { LayoutSummary, SynFormDataService } from 'syn-form';

/** Maintainer home: every layout, its published version, its draft — plus create/clone. */
@Component({
  selector: 'app-layout-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <header class="page-head">
      <h1>Layouts</h1>
      <button class="btn primary" (click)="creating.set(!creating())">New Layout</button>
    </header>

    @if (creating()) {
      <div class="create-row">
        <input placeholder="layout-key (kebab-case)" [(ngModel)]="newKey" />
        <input placeholder="Title" [(ngModel)]="newTitle" />
        <button class="btn primary" (click)="create()">Create draft v1</button>
        @if (createError()) { <span class="err">{{ createError() }}</span> }
      </div>
    }

    <table class="grid">
      <thead><tr><th>Layout key</th><th>Title</th><th>Published</th><th>Draft</th><th></th></tr></thead>
      <tbody>
        @for (l of layouts(); track l.layoutKey) {
          <tr>
            <td><a [routerLink]="['/maintainer', l.layoutKey]">{{ l.layoutKey }}</a></td>
            <td>{{ l.title }}</td>
            <td>@if (l.latestPublishedVersion) { v{{ l.latestPublishedVersion }} <small>{{ l.publishedAt | date:'medium' }}</small> } @else { — }</td>
            <td>@if (l.draftVersion) { <span class="badge draft">DRAFT v{{ l.draftVersion }}</span> } @else { — }</td>
            <td class="row-actions">
              <button class="btn sm" [routerLink]="['/maintainer', l.layoutKey]">Edit</button>
              <button class="btn sm ghost" (click)="clone(l)">Clone</button>
            </td>
          </tr>
        } @empty {
          <tr><td colspan="5" class="empty">No layouts yet.</td></tr>
        }
      </tbody>
    </table>
  `,
  styles: [`
    .page-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
    h1 { font-size: 22px; margin: 0; }
    .btn { border: 1.5px solid #d1d5db; background: #fff; border-radius: 8px; padding: 8px 16px; font: inherit; font-size: 14px; cursor: pointer; }
    .btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; font-weight: 600; }
    .btn.sm { padding: 4px 12px; font-size: 13px; }
    .btn.ghost { background: transparent; }
    .create-row { display: flex; gap: 10px; margin-bottom: 16px; align-items: center; }
    .create-row input { border: 1.5px solid #d1d5db; border-radius: 8px; padding: 8px 12px; font: inherit; font-size: 14px; }
    .err { color: #dc2626; font-size: 13px; }
    .grid { width: 100%; border-collapse: collapse; font-size: 14.5px; }
    .grid th { text-align: left; font-size: 12.5px; text-transform: uppercase; letter-spacing: .05em; color: #6b7280; padding: 8px 12px; border-bottom: 2px solid #e5e7eb; }
    .grid td { padding: 10px 12px; border-bottom: 1px solid #f3f4f6; }
    .grid a { color: #1d4ed8; font-weight: 600; text-decoration: none; }
    .grid small { color: #9ca3af; margin-left: 6px; }
    .badge.draft { background: #fef3c7; color: #92400e; border-radius: 6px; padding: 2px 10px; font-size: 12.5px; font-weight: 700; }
    .row-actions { display: flex; gap: 8px; }
    .empty { color: #9ca3af; text-align: center; padding: 24px; }
  `],
})
export class LayoutListPage implements OnInit {
  private data = inject(SynFormDataService);
  private router = inject(Router);

  layouts = signal<LayoutSummary[]>([]);
  creating = signal(false);
  createError = signal('');
  newKey = '';
  newTitle = '';

  async ngOnInit(): Promise<void> {
    this.layouts.set(await this.data.listLayouts());
  }

  async create(): Promise<void> {
    this.createError.set('');
    try {
      await this.data.createLayout(this.newKey.trim(), this.newTitle.trim());
      await this.router.navigate(['/maintainer', this.newKey.trim()]);
    } catch (e: unknown) {
      this.createError.set((e as { error?: { error?: string } })?.error?.error ?? 'Create failed.');
    }
  }

  async clone(l: LayoutSummary): Promise<void> {
    const newKey = prompt(`Clone '${l.layoutKey}' as (new layout-key):`, `${l.layoutKey}-copy`);
    if (!newKey) return;
    try {
      await this.data.cloneLayout(l.layoutKey, newKey);
      this.layouts.set(await this.data.listLayouts());
    } catch (e: unknown) {
      this.createError.set((e as { error?: { error?: string } })?.error?.error ?? 'Clone failed.');
    }
  }
}
