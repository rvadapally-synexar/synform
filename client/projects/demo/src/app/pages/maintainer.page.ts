import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  Condition, ConditionOp, FieldDef, LayoutDef, LayoutEnvelope,
  PointerModeService, SynFormComponent, SynFormDataService,
} from 'syn-form';

const CONTROL_TYPES = ['text', 'textarea', 'number', 'dropdown', 'multiselect', 'radio', 'checkbox', 'checkboxGroup', 'date', 'time', 'bpPair'] as const;
const DATA_TYPE_FOR: Record<string, string> = {
  text: 'string', textarea: 'string', number: 'number', dropdown: 'string',
  multiselect: 'string[]', radio: 'string', checkbox: 'boolean',
  checkboxGroup: 'string[]', date: 'date', time: 'time', bpPair: 'bpPair',
};
const OPS: ConditionOp[] = ['eq', 'neq', 'in', 'contains', 'gt', 'lt', 'gte', 'lte', 'notEmpty'];

/**
 * Maintainer (spec §9): left pane = field grid editing the DRAFT layout;
 * right pane = a real <syn-form> rendering that draft in memory, re-rendered
 * on every edit. Publish runs server-side §4.6 validation; versions are explicit.
 */
@Component({
  selector: 'app-maintainer',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, SynFormComponent],
  template: `
    <header class="page-head">
      <h1><a routerLink="/maintainer">Layouts</a> / {{ layoutKey }}</h1>
      <div class="actions">
        @if (draft()) {
          <button class="btn ghost" (click)="discard()">Discard draft</button>
          <button class="btn" (click)="saveDraft()" [disabled]="!dirty()">Save Draft</button>
          <button class="btn primary" (click)="publish()">Publish v{{ draft()!.version }}</button>
        } @else if (publishedVersion()) {
          <span class="badge pub">PUBLISHED v{{ publishedVersion() }}</span>
          <button class="btn primary" (click)="createDraft()">Create Draft v{{ publishedVersion()! + 1 }}</button>
        }
        <button class="btn ghost" (click)="schemaOpen.set(!schemaOpen())">View JSON Schema</button>
      </div>
    </header>

    @if (message()) { <div class="banner" [class.ok]="!messageIsError()" [class.bad]="messageIsError()">{{ message() }}</div> }
    @for (e of publishErrors(); track e) { <div class="banner bad">{{ e }}</div> }

    @if (draft(); as d) {
      <div class="panes">
        <section class="pane left">
          <div class="pane-head">
            <h2>Fields <small>(draft v{{ d.version }})</small></h2>
            <button class="btn sm" (click)="addField()">+ Add field</button>
          </div>
          <div class="field-list">
            @for (f of def.fields; track $index; let i = $index) {
              <div class="field-card" [class.open]="expanded() === i">
                <div class="field-row" (click)="expanded.set(expanded() === i ? -1 : i)">
                  <span class="drag">
                    <button class="mini" (click)="move(i, -1); $event.stopPropagation()" [disabled]="i === 0">▲</button>
                    <button class="mini" (click)="move(i, 1); $event.stopPropagation()" [disabled]="i === def.fields.length - 1">▼</button>
                  </span>
                  <code class="fname" [class.locked]="isPublishedField(f.name)">{{ f.name || '(unnamed)' }}</code>
                  <input class="inline-label" [(ngModel)]="f.label" (ngModelChange)="touch()" (click)="$event.stopPropagation()" placeholder="Label" />
                  <select [(ngModel)]="f.controlType" (ngModelChange)="controlTypeChanged(f)" (click)="$event.stopPropagation()">
                    @for (ct of controlTypes; track ct) { <option [value]="ct">{{ ct }}</option> }
                  </select>
                  <label class="req-toggle" (click)="$event.stopPropagation()">
                    <input type="checkbox" [(ngModel)]="f.required" (ngModelChange)="touch()" /> req
                  </label>
                  <button class="mini del" (click)="removeField(i); $event.stopPropagation()" title="Remove field">✕</button>
                </div>
                @if (expanded() === i) {
                  <div class="field-detail">
                    @if (!isPublishedField(f.name)) {
                      <label>Name <input [(ngModel)]="f.name" (ngModelChange)="touch()" placeholder="camelCase" /></label>
                    } @else {
                      <label>Name <input [value]="f.name" disabled title="Stable contract — never renamed after publish" /></label>
                    }
                    <label>Group <input [(ngModel)]="f.group" (ngModelChange)="touch()" placeholder="e.g. assessment" /></label>
                    <label>Unit <input [(ngModel)]="f.unit" (ngModelChange)="touch()" placeholder="kg" /></label>
                    <label>Min <input [(ngModel)]="f.min" (ngModelChange)="touch()" placeholder="number or today" /></label>
                    <label>Max <input [(ngModel)]="f.max" (ngModelChange)="touch()" placeholder="number or today" /></label>
                    <label>Aliases (comma-sep)
                      <input [ngModel]="(f.aliases ?? []).join(', ')" (ngModelChange)="setAliases(f, $event)" placeholder="BP, blood pressure" />
                    </label>

                    @if (hasOptions(f)) {
                      <div class="opts">
                        <label class="opt-mode">
                          Options:
                          <select [ngModel]="optionMode(f)" (ngModelChange)="setOptionMode(f, $event)">
                            <option value="inline">inline</option>
                            <option value="lookup">lookup</option>
                          </select>
                        </label>
                        @if (optionMode(f) === 'lookup') {
                          <select [ngModel]="f.options?.lookupKey ?? ''" (ngModelChange)="setLookupKey(f, $event)">
                            <option value="" disabled>choose lookup…</option>
                            @for (k of lookupKeys(); track k) { <option [value]="k">{{ k }}</option> }
                          </select>
                          <label>filterBy
                            <select [ngModel]="f.filterBy ?? ''" (ngModelChange)="f.filterBy = $event || null; touch()">
                              <option value="">—</option>
                              @for (other of def.fields; track other.name) {
                                @if (other.name !== f.name) { <option [value]="other.name">{{ other.name }}</option> }
                              }
                            </select>
                          </label>
                        } @else {
                          @for (o of f.options?.inline ?? []; track $index; let oi = $index) {
                            <div class="opt-row">
                              <input [(ngModel)]="o.value" (ngModelChange)="touch()" placeholder="value" />
                              <input [(ngModel)]="o.label" (ngModelChange)="touch()" placeholder="label" />
                              <button class="mini del" (click)="f.options!.inline!.splice(oi, 1); touch()">✕</button>
                            </div>
                          }
                          <button class="btn sm" (click)="addOption(f)">+ option</button>
                        }
                      </div>
                    }

                    <div class="cond">
                      <span class="cond-title">visibleWhen</span>
                      <ng-container *ngTemplateOutlet="condEditor; context: { f, key: 'visibleWhen' }" />
                      <span class="cond-title">requiredWhen</span>
                      <ng-container *ngTemplateOutlet="condEditor; context: { f, key: 'requiredWhen' }" />
                    </div>
                  </div>
                }
              </div>
            }
          </div>
        </section>

        <section class="pane right">
          <div class="pane-head">
            <h2>Live preview</h2>
            <label class="touch-toggle">
              <input type="checkbox" [ngModel]="touchPreview()" (ngModelChange)="setTouchPreview($event)" />
              Preview on touch
            </label>
          </div>
          <div class="preview-banner">DRAFT v{{ d.version }} — unpublished</div>
          <div class="preview-host" [class.ipad]="touchPreview()">
            @if (previewLayout(); as pl) {
              <syn-form [layoutKey]="layoutKey" [layout]="pl" />
            }
          </div>
        </section>
      </div>
    } @else if (publishedVersion()) {
      <p class="hint">No draft. The published version is live. Create a draft to make changes —
        publishing is always an explicit action; live forms keep rendering v{{ publishedVersion() }} until you publish.</p>
    }

    @if (schemaOpen()) {
      <aside class="drawer" (click)="schemaOpen.set(false)">
        <div class="drawer-body" (click)="$event.stopPropagation()">
          <h2>JSON Schema <small>— the machine/agent surface (GET /api/layouts/{{ layoutKey }}/schema)</small></h2>
          <pre>{{ schemaJson() }}</pre>
        </div>
      </aside>
    }

    <ng-template #condEditor let-f="f" let-key="key">
      <div class="cond-row">
        <select [ngModel]="cond(f, key)?.field ?? ''" (ngModelChange)="setCond(f, key, 'field', $event)">
          <option value="">— none —</option>
          @for (other of def.fields; track other.name) {
            @if (other.name !== f.name) { <option [value]="other.name">{{ other.name }}</option> }
          }
        </select>
        @if (cond(f, key); as c) {
          <select [ngModel]="c.op" (ngModelChange)="setCond(f, key, 'op', $event)">
            @for (op of ops; track op) { <option [value]="op">{{ op }}</option> }
          </select>
          @if (c.op !== 'notEmpty') {
            <input [ngModel]="condValue(c)" (ngModelChange)="setCond(f, key, 'value', $event)"
                   placeholder='value (a,b for lists)' />
          }
        }
      </div>
    </ng-template>
  `,
  styles: [`
    .page-head { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 10px; margin-bottom: 12px; }
    h1 { font-size: 20px; margin: 0; } h1 a { color: #1d4ed8; text-decoration: none; }
    h2 { font-size: 15px; margin: 0; } h2 small { color: #9ca3af; font-weight: 400; }
    .actions { display: flex; gap: 8px; align-items: center; }
    .btn { border: 1.5px solid #d1d5db; background: #fff; border-radius: 8px; padding: 7px 14px; font: inherit; font-size: 13.5px; cursor: pointer; }
    .btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; font-weight: 600; }
    .btn.ghost { background: transparent; } .btn.sm { padding: 4px 10px; font-size: 12.5px; }
    .btn:disabled { opacity: .45; cursor: default; }
    .badge.pub { background: #dcfce7; color: #166534; border-radius: 6px; padding: 4px 12px; font-size: 12.5px; font-weight: 700; }
    .banner { border-radius: 8px; padding: 9px 14px; margin-bottom: 8px; font-size: 13.5px; }
    .banner.ok { background: #f0fdf4; border: 1px solid #86efac; color: #166534; }
    .banner.bad { background: #fef2f2; border: 1px solid #fecaca; color: #b91c1c; }
    .panes { display: grid; grid-template-columns: minmax(480px, 1fr) minmax(380px, 560px); gap: 24px; align-items: start; }
    @media (max-width: 1100px) { .panes { grid-template-columns: 1fr; } }
    .pane-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; }
    .field-card { border: 1px solid #e5e7eb; border-radius: 10px; margin-bottom: 6px; background: #fff; }
    .field-card.open { border-color: #93c5fd; box-shadow: 0 2px 8px rgb(37 99 235 / .08); }
    .field-row { display: flex; align-items: center; gap: 8px; padding: 7px 10px; cursor: pointer; }
    .drag { display: flex; flex-direction: column; gap: 1px; }
    .mini { border: none; background: #f3f4f6; border-radius: 4px; font-size: 9px; padding: 1px 6px; cursor: pointer; }
    .mini.del { color: #dc2626; font-size: 12px; padding: 3px 8px; }
    .fname { font-size: 12.5px; background: #eff6ff; color: #1e40af; padding: 3px 8px; border-radius: 6px; min-width: 110px; }
    .fname.locked::after { content: ' 🔒'; font-size: 10px; }
    .inline-label { flex: 1; border: 1px solid #e5e7eb; border-radius: 6px; padding: 5px 8px; font: inherit; font-size: 13.5px; }
    .field-row select { border: 1px solid #e5e7eb; border-radius: 6px; padding: 5px; font-size: 12.5px; }
    .req-toggle { font-size: 12px; color: #6b7280; display: flex; gap: 3px; align-items: center; }
    .field-detail { border-top: 1px dashed #e5e7eb; padding: 10px 12px; display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px 12px; font-size: 12.5px; }
    .field-detail label { display: flex; flex-direction: column; gap: 3px; color: #6b7280; }
    .field-detail input, .field-detail select { border: 1px solid #e5e7eb; border-radius: 6px; padding: 5px 8px; font: inherit; font-size: 13px; }
    .opts { grid-column: 1 / -1; display: flex; flex-direction: column; gap: 6px; background: #f9fafb; border-radius: 8px; padding: 8px 10px; }
    .opt-mode { flex-direction: row !important; align-items: center; gap: 8px !important; }
    .opt-row { display: flex; gap: 6px; }
    .cond { grid-column: 1 / -1; display: flex; flex-direction: column; gap: 5px; background: #fffbeb; border-radius: 8px; padding: 8px 10px; }
    .cond-title { font-size: 11.5px; font-weight: 700; color: #92400e; text-transform: uppercase; letter-spacing: .04em; }
    .cond-row { display: flex; gap: 6px; }
    .cond-row select, .cond-row input { border: 1px solid #e5e7eb; border-radius: 6px; padding: 5px 8px; font-size: 12.5px; }
    .preview-banner { background: #fef3c7; color: #92400e; font-weight: 700; font-size: 12.5px; border-radius: 8px 8px 0 0; padding: 6px 14px; }
    .preview-host { border: 1px solid #e5e7eb; border-top: none; border-radius: 0 0 10px 10px; padding: 16px; background: #fff; }
    .preview-host.ipad { max-width: 500px; }
    .touch-toggle { font-size: 13px; color: #374151; display: flex; gap: 6px; align-items: center; }
    .hint { color: #6b7280; font-size: 14px; }
    .drawer { position: fixed; inset: 0; background: rgb(0 0 0 / .35); z-index: 900; display: flex; justify-content: flex-end; }
    .drawer-body { background: #fff; width: min(680px, 90vw); padding: 20px 24px; overflow-y: auto; }
    .drawer-body pre { background: #0f172a; color: #e2e8f0; border-radius: 10px; padding: 16px; font-size: 12px; overflow-x: auto; }
  `],
})
export class MaintainerPage implements OnInit, OnDestroy {
  private data = inject(SynFormDataService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private pointerMode = inject(PointerModeService);

  layoutKey = '';
  controlTypes = CONTROL_TYPES;
  ops = OPS;

  draft = signal<LayoutEnvelope | null>(null);
  publishedVersion = signal<number | null>(null);
  previewLayout = signal<LayoutDef | null>(null);
  lookupKeys = signal<string[]>([]);
  expanded = signal(-1);
  dirty = signal(false);
  message = signal('');
  messageIsError = signal(false);
  publishErrors = signal<string[]>([]);
  schemaOpen = signal(false);
  schemaJson = signal('');
  touchPreview = signal(false);

  def: LayoutDef = { fields: [] };
  private publishedNames = new Set<string>();
  private previewTimer?: ReturnType<typeof setTimeout>;

  async ngOnInit(): Promise<void> {
    this.layoutKey = this.route.snapshot.paramMap.get('layoutKey')!;
    this.lookupKeys.set(await this.data.listLookupKeys());
    await this.load();
  }

  ngOnDestroy(): void {
    this.pointerMode.setOverride(null);
    clearTimeout(this.previewTimer);
  }

  private async load(): Promise<void> {
    try {
      const published = await this.data.getLayout(this.layoutKey, 'latestPublished');
      this.publishedVersion.set(published.version);
      this.publishedNames = new Set(published.json.fields.map(f => f.name));
    } catch { this.publishedVersion.set(null); }

    try {
      const draft = await this.data.getLayout(this.layoutKey, 'draft');
      this.draft.set(draft);
      this.def = draft.json;
      this.refreshPreview(true);
    } catch { this.draft.set(null); }

    void this.loadSchema();
  }

  private async loadSchema(): Promise<void> {
    try {
      const schema = await this.data.getSchema(this.layoutKey, this.draft() ? 'draft' : 'latestPublished');
      this.schemaJson.set(JSON.stringify(schema, null, 2));
    } catch { this.schemaJson.set('(no published version or draft yet)'); }
  }

  // --- editing ---

  touch(): void {
    this.dirty.set(true);
    this.refreshPreview();
  }

  /** The instant feedback: debounce ~300ms, then hand the in-memory draft to the preview <syn-form>. */
  private refreshPreview(immediate = false): void {
    clearTimeout(this.previewTimer);
    this.previewTimer = setTimeout(() => {
      this.previewLayout.set(structuredClone(this.def));
    }, immediate ? 0 : 300);
  }

  isPublishedField(name: string): boolean {
    return this.publishedNames.has(name);
  }

  addField(): void {
    this.def.fields.push({
      name: '', label: '', controlType: 'text', dataType: 'string',
      group: 'default', order: (Math.max(0, ...this.def.fields.map(f => f.order ?? 0)) + 10),
    });
    this.expanded.set(this.def.fields.length - 1);
    this.touch();
  }

  removeField(i: number): void {
    this.def.fields.splice(i, 1);
    this.touch();
  }

  move(i: number, dir: number): void {
    const [f] = this.def.fields.splice(i, 1);
    this.def.fields.splice(i + dir, 0, f);
    this.def.fields.forEach((x, idx) => x.order = (idx + 1) * 10);
    this.touch();
  }

  controlTypeChanged(f: FieldDef): void {
    f.dataType = DATA_TYPE_FOR[f.controlType] as FieldDef['dataType'];
    if (!this.hasOptions(f)) { f.options = undefined; f.filterBy = null; }
    else f.options ??= { inline: [] };
    this.touch();
  }

  hasOptions(f: FieldDef): boolean {
    return ['dropdown', 'multiselect', 'radio', 'checkboxGroup'].includes(f.controlType);
  }

  optionMode(f: FieldDef): 'inline' | 'lookup' {
    return f.options?.lookupKey ? 'lookup' : 'inline';
  }

  setOptionMode(f: FieldDef, mode: 'inline' | 'lookup'): void {
    f.options = mode === 'lookup' ? { lookupKey: '' } : { inline: [] };
    this.touch();
  }

  setLookupKey(f: FieldDef, key: string): void {
    f.options = { lookupKey: key };
    this.touch();
  }

  addOption(f: FieldDef): void {
    (f.options ??= { inline: [] }).inline!.push({ value: '', label: '' });
    this.touch();
  }

  setAliases(f: FieldDef, raw: string): void {
    f.aliases = raw.split(',').map(s => s.trim()).filter(Boolean);
    this.touch();
  }

  // --- condition builder (single condition; one-level and/or stays JSON-editable) ---

  cond(f: FieldDef, key: 'visibleWhen' | 'requiredWhen'): Condition | null {
    return (f[key] as Condition | null) ?? null;
  }

  condValue(c: Condition): string {
    return Array.isArray(c.value) ? c.value.join(', ') : String(c.value ?? '');
  }

  setCond(f: FieldDef, key: 'visibleWhen' | 'requiredWhen', part: 'field' | 'op' | 'value', value: string): void {
    if (part === 'field' && !value) { f[key] = null; this.touch(); return; }
    const c: Condition = (f[key] as Condition | null) ?? { op: 'eq' };
    if (part === 'field') c.field = value;
    if (part === 'op') c.op = value as ConditionOp;
    if (part === 'value') {
      const needsList = c.op === 'in';
      c.value = needsList ? value.split(',').map(s => s.trim()).filter(Boolean) : value;
    }
    f[key] = c;
    this.touch();
  }

  // --- preview touch toggle ---

  setTouchPreview(on: boolean): void {
    this.touchPreview.set(on);
    this.pointerMode.setOverride(on ? true : null);
  }

  // --- draft lifecycle (explicit, user-driven versioning) ---

  async createDraft(): Promise<void> {
    const draft = await this.data.createDraft(this.layoutKey);
    this.draft.set(draft);
    this.def = draft.json;
    this.refreshPreview(true);
    this.note(`Draft v${draft.version} created from published v${this.publishedVersion()}.`);
  }

  async saveDraft(): Promise<void> {
    const current = this.draft();
    if (!current) return;
    try {
      const saved = await this.data.saveDraft(this.layoutKey, this.def, current.updatedAt);
      this.draft.set(saved);
      this.dirty.set(false);
      this.publishErrors.set([]);
      this.note('Draft saved.');
      void this.loadSchema();
    } catch (e: unknown) {
      const err = e as { status?: number; error?: { error?: string } };
      this.note(err?.error?.error ?? 'Save failed.', true);
    }
  }

  async publish(): Promise<void> {
    if (this.dirty()) await this.saveDraft();
    const result = await this.data.publish(this.layoutKey);
    if (result.ok) {
      this.publishErrors.set([]);
      this.note(`Published v${result.layout!.version}. Live forms pick it up on next load.`);
      await this.load();
    } else {
      this.publishErrors.set(result.errors ?? []);
    }
  }

  async discard(): Promise<void> {
    if (!confirm('Discard this draft? Unpublished changes will be lost.')) return;
    await this.data.discardDraft(this.layoutKey);
    if (!this.publishedVersion()) { await this.router.navigate(['/maintainer']); return; }
    await this.load();
  }

  private note(text: string, isError = false): void {
    this.message.set(text);
    this.messageIsError.set(isError);
    setTimeout(() => this.message.set(''), 4000);
  }
}
