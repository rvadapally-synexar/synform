import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { LookupItem, SynFormDataService } from 'syn-form';

/** Lookup management (spec §9): CRUD grid on lookup_item. Delete = deactivate (referenced by published layouts/records). */
@Component({
  selector: 'app-lookups',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <header class="page-head">
      <h1>Lookups</h1>
      <div class="key-bar">
        @for (k of keys(); track k) {
          <button class="key" [class.on]="selectedKey() === k" (click)="select(k)">{{ k }}</button>
        }
        <input class="new-key" placeholder="new lookup key…" [(ngModel)]="newKey" (keydown.enter)="select(newKey)" />
      </div>
    </header>

    @if (selectedKey()) {
      <table class="grid">
        <thead><tr><th>Value</th><th>Label</th><th>Parent key</th><th>Order</th><th>Active</th><th></th></tr></thead>
        <tbody>
          @for (item of items(); track item.id) {
            <tr [class.inactive]="!item.active">
              <td><code>{{ item.value }}</code></td>
              <td><input [(ngModel)]="item.label" (blur)="update(item)" /></td>
              <td><input [(ngModel)]="item.parentKey" (blur)="update(item)" placeholder="—" /></td>
              <td><input class="num" type="number" [(ngModel)]="item.sortOrder" (blur)="update(item)" /></td>
              <td><input type="checkbox" [(ngModel)]="item.active" (ngModelChange)="update(item)" /></td>
              <td><button class="mini del" (click)="deactivate(item)" title="Deactivate">✕</button></td>
            </tr>
          }
          <tr class="add-row">
            <td><input [(ngModel)]="draft.value" placeholder="value" /></td>
            <td><input [(ngModel)]="draft.label" placeholder="label" /></td>
            <td><input [(ngModel)]="draft.parentKey" placeholder="parent (optional)" /></td>
            <td><input class="num" type="number" [(ngModel)]="draft.sortOrder" /></td>
            <td></td>
            <td><button class="btn sm primary" (click)="create()">Add</button></td>
          </tr>
        </tbody>
      </table>
      @if (error()) { <div class="err">{{ error() }}</div> }
      <p class="hint">Items are never hard-deleted — published layouts and saved records may reference them. ✕ deactivates.</p>
    } @else {
      <p class="hint">Select a lookup key, or type a new one and press Enter.</p>
    }
  `,
  styles: [`
    .page-head { margin-bottom: 16px; }
    h1 { font-size: 22px; margin: 0 0 12px; }
    .key-bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .key { border: 1.5px solid #d1d5db; background: #fff; border-radius: 18px; padding: 6px 16px; font: inherit; font-size: 13.5px; cursor: pointer; }
    .key.on { background: #1d4ed8; border-color: #1d4ed8; color: #fff; }
    .new-key { border: 1.5px dashed #d1d5db; border-radius: 18px; padding: 6px 16px; font: inherit; font-size: 13.5px; }
    .grid { width: 100%; border-collapse: collapse; font-size: 14px; }
    .grid th { text-align: left; font-size: 12px; text-transform: uppercase; letter-spacing: .05em; color: #6b7280; padding: 8px 10px; border-bottom: 2px solid #e5e7eb; }
    .grid td { padding: 6px 10px; border-bottom: 1px solid #f3f4f6; }
    .grid input { border: 1px solid #e5e7eb; border-radius: 6px; padding: 5px 8px; font: inherit; font-size: 13.5px; width: 100%; box-sizing: border-box; }
    .grid input.num { width: 70px; }
    .grid input[type=checkbox] { width: auto; }
    tr.inactive td { opacity: .45; }
    .add-row td { background: #f9fafb; }
    .btn { border: 1.5px solid #d1d5db; background: #fff; border-radius: 8px; padding: 6px 14px; font: inherit; font-size: 13px; cursor: pointer; }
    .btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; }
    .mini { border: none; background: #f3f4f6; border-radius: 4px; padding: 3px 8px; cursor: pointer; }
    .mini.del { color: #dc2626; }
    .err { color: #dc2626; font-size: 13.5px; margin-top: 8px; }
    .hint { color: #9ca3af; font-size: 13px; margin-top: 12px; }
  `],
})
export class LookupsPage implements OnInit {
  private data = inject(SynFormDataService);

  keys = signal<string[]>([]);
  selectedKey = signal('');
  items = signal<LookupItem[]>([]);
  error = signal('');
  newKey = '';
  draft: Partial<LookupItem> = { sortOrder: 0 };

  async ngOnInit(): Promise<void> {
    this.keys.set(await this.data.listLookupKeys());
  }

  async select(key: string): Promise<void> {
    key = key.trim();
    if (!key) return;
    this.selectedKey.set(key);
    this.newKey = '';
    this.items.set(await this.data.getLookup(key, undefined, true));
  }

  async create(): Promise<void> {
    this.error.set('');
    try {
      await this.data.createLookupItem({
        lookupKey: this.selectedKey(),
        value: this.draft.value, label: this.draft.label,
        parentKey: this.draft.parentKey || null,
        sortOrder: this.draft.sortOrder ?? 0, active: true,
      });
      this.draft = { sortOrder: 0 };
      await this.select(this.selectedKey());
      this.keys.set(await this.data.listLookupKeys());
    } catch (e: unknown) {
      this.error.set((e as { error?: { error?: string } })?.error?.error ?? 'Create failed.');
    }
  }

  async update(item: LookupItem): Promise<void> {
    await this.data.updateLookupItem(item.id, {
      label: item.label, parentKey: item.parentKey || null,
      sortOrder: item.sortOrder, active: item.active,
    });
  }

  async deactivate(item: LookupItem): Promise<void> {
    await this.data.deactivateLookupItem(item.id);
    await this.select(this.selectedKey());
  }
}
