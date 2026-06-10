import { CommonModule } from '@angular/common';
import {
  AfterContentInit, ChangeDetectionStrategy, Component, ElementRef, TemplateRef, ViewContainerRef,
  computed, contentChildren, effect, inject, input, output, signal, untracked, viewChild,
} from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { evaluateCondition, isEmpty, referencedFields } from './condition';
import { COMPUTED_FNS } from './computed';
import { SynFormDataService } from './data.service';
import { SynFieldComponent } from './field.component';
import { SynFormGroupDirective } from './group.directive';
import { PointerModeService } from './pointer';
import {
  FieldDef, FieldHighlight, FormRecord, LayoutDef, OptionItem, PartialValues,
  PopulateResult, PopulateSource, ProvenanceEntry, SaveResult,
} from './types';

/**
 * <syn-form> — schema-driven clinical form engine (spec §5).
 * One reusable component: renders fields from a versioned layout, exposes a Reactive
 * FormGroup, generic save, and the populate() seam shared by voice/photo/agent input.
 */
@Component({
  selector: 'syn-form',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, SynFieldComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #groupTpl let-group="group">
      @for (f of visibleFieldsFor(group); track f.name) {
        <syn-field
          [field]="f"
          [control]="controlFor(f.name)"
          [options]="optionsFor(f.name)"
          [coarse]="pointer.coarse()"
          [density]="density()"
          [highlight]="highlightFor(f.name)"
          [errors]="errorsFor(f.name)"
          (interacted)="onInteracted(f.name)" />
      }
    </ng-template>

    <div class="syn-form-root" [class.coarse]="pointer.coarse()">
      @if (loadError()) { <div class="syn-form-error" role="alert">{{ loadError() }}</div> }
      <ng-content />
      <!-- Groups without a host-provided slot render here, in order. -->
      <div #defaultHost class="syn-form-default-groups"></div>
      @if (formErrors().length) {
        <div class="syn-form-errors" role="alert">
          @for (e of formErrors(); track e) { <div>{{ e }}</div> }
        </div>
      }
    </div>
  `,
  styles: [`
    .syn-form-root { container-type: inline-size; }
    /* Container queries, not viewport queries (spec §5): the component adapts to its own width. */
    .syn-form-default-groups, ::ng-deep [synFormGroup] {
      display: grid;
      grid-template-columns: 1fr 1fr;
      column-gap: 20px;
      align-content: start;
    }
    @container (max-width: 900px) {
      .syn-form-default-groups, ::ng-deep [synFormGroup] { grid-template-columns: 1fr; }
    }
    .syn-form-error, .syn-form-errors {
      background: #fef2f2; border: 1px solid #fecaca; color: #b91c1c;
      padding: 10px 14px; border-radius: 8px; margin: 8px 0; font-size: 14px;
    }
  `],
})
export class SynFormComponent implements AfterContentInit {
  // --- Public API (spec §5 + v1.1 additions) ---
  layoutKey = input.required<string>();
  version = input<number | 'latestPublished'>('latestPublished');
  density = input<'comfortable' | 'compact'>('comfortable');
  /** In-memory layout override — bypasses fetching. This is what makes the Maintainer live preview possible. */
  layout = input<LayoutDef | null>(null);
  /** Host-supplied linkage saved with the record, e.g. { encounterId }. */
  context = input<unknown>(undefined);

  saved = output<{ recordId: string }>();
  dirtyChange = output<boolean>();
  populated = output<PopulateResult>();

  protected pointer = inject(PointerModeService);
  private data = inject(SynFormDataService);
  private host = inject(ElementRef<HTMLElement>);

  private groupTpl = viewChild.required<TemplateRef<{ group: string }>>('groupTpl');
  private defaultHost = viewChild.required('defaultHost', { read: ViewContainerRef });
  private slots = contentChildren(SynFormGroupDirective, { descendants: true });

  private layoutDef = signal<LayoutDef | null>(null);
  private resolvedVersion = signal<number>(0);
  protected loadError = signal<string | null>(null);
  private values = signal<PartialValues>({});
  private lookups = signal<Record<string, OptionItem[]>>({});     // lookupKey → all active items
  private highlights = signal<Record<string, FieldHighlight>>({});
  private clientErrors = signal<Record<string, string[]>>({});
  private serverErrors = signal<Record<string, string[]>>({});
  private touched = signal<Record<string, boolean>>({});
  private saveAttempted = signal(false);

  private formGroup = new FormGroup<Record<string, FormControl>>({});
  private valueSub?: Subscription;
  private provenance: Record<string, ProvenanceEntry> = {};
  private populating = false;
  private recordId: string = crypto.randomUUID();
  private contentReady = false;

  constructor() {
    // (Re)load when layoutKey/version change, or when an in-memory draft layout is supplied.
    effect(() => {
      const inline = this.layout();
      const key = this.layoutKey();
      const version = this.version();
      untracked(() => inline ? this.applyLayout(inline, 0) : this.fetchLayout(key, version));
    });
    // Keyboard avoidance, implemented once here: keep the focused control visible
    // above the on-screen keyboard (works in browsers and the Capacitor shell alike).
    this.host.nativeElement.addEventListener('focusin', (e: FocusEvent) => {
      const t = e.target as HTMLElement;
      if (t.matches('input, select, textarea, button'))
        setTimeout(() => t.scrollIntoView({ block: 'center', behavior: 'smooth' }), 250);
    });
  }

  ngAfterContentInit(): void {
    this.contentReady = true;
    if (this.layoutDef()) this.renderGroups();
  }

  // ---------- loading ----------

  private async fetchLayout(key: string, version: number | 'latestPublished'): Promise<void> {
    try {
      const envelope = await this.data.getLayout(key, version);
      this.applyLayout(envelope.json, envelope.version);
    } catch {
      this.loadError.set(`Layout '${key}' (${version}) could not be loaded. Is the API running and the layout published?`);
    }
  }

  private async applyLayout(def: LayoutDef, version: number): Promise<void> {
    this.loadError.set(null);
    this.resolvedVersion.set(version);
    await this.loadLookups(def);
    this.buildForm(def);
    this.layoutDef.set(def);
    if (this.contentReady) this.renderGroups();
  }

  private async loadLookups(def: LayoutDef): Promise<void> {
    const keys = [...new Set(def.fields.map(f => f.options?.lookupKey).filter((k): k is string => !!k))];
    const entries = await Promise.all(keys.map(async k => {
      try { return [k, await this.data.getLookup(k)] as const; }
      catch { return [k, []] as const; }   // draft may reference a not-yet-created lookup; degrade gracefully
    }));
    this.lookups.set(Object.fromEntries(entries.map(([k, items]) =>
      [k, items.map(i => ({ value: i.value, label: i.label, parentKey: i.parentKey } as OptionItem & { parentKey?: string | null }))])));
  }

  private buildForm(def: LayoutDef): void {
    this.valueSub?.unsubscribe();
    const previous = this.formGroup.getRawValue();
    const controls: Record<string, FormControl> = {};
    for (const f of def.fields)
      controls[f.name] = new FormControl({ value: previous[f.name] ?? null, disabled: !!f.computedFrom });
    this.formGroup = new FormGroup(controls);
    this.provenance = Object.fromEntries(Object.entries(this.provenance).filter(([k]) => k in controls));
    this.values.set(this.formGroup.getRawValue());

    this.valueSub = this.formGroup.valueChanges.subscribe(() => {
      const raw = this.formGroup.getRawValue();
      if (!this.populating) {
        for (const [name, value] of Object.entries(raw))
          if (value !== this.values()[name] && !(value == null && this.values()[name] == null))
            this.provenance[name] = { source: 'manual', ts: new Date().toISOString() };
      }
      this.values.set(raw);
      this.recomputeDerived(def);
      this.applyCascades(def);
      this.applyEnabledWhen(def);
      if (this.saveAttempted()) this.clientErrors.set(this.validateClient(def));
      this.dirtyChange.emit(this.formGroup.dirty);
    });

    this.recomputeDerived(def);
    this.applyEnabledWhen(def);
  }

  // ---------- rendering ----------

  private renderGroups(): void {
    const def = this.layoutDef();
    if (!def) return;
    const groups = [...new Set(def.fields.map(f => f.group ?? 'default'))];
    const slots = this.slots();
    const container = this.defaultHost();
    container.clear();

    // All views live in the component's own container (change detection owns them);
    // slotted groups are then ported into the host-provided elements — a DOM move,
    // exactly how CDK portals work, so bindings keep updating.
    for (const slot of slots) {
      slot.element.replaceChildren();
      const view = container.createEmbeddedView(this.groupTpl(), { group: slot.group() });
      view.detectChanges();
      for (const node of view.rootNodes as Node[]) slot.element.appendChild(node);
    }
    const slotted = new Set(slots.map(s => s.group()));
    for (const g of groups.filter(g => !slotted.has(g)))
      container.createEmbeddedView(this.groupTpl(), { group: g });
  }

  protected visibleFieldsFor(group: string): FieldDef[] {
    const def = this.layoutDef();
    if (!def) return [];
    const vals = this.values();
    return def.fields
      .filter(f => (f.group ?? 'default') === group)
      .filter(f => !f.visibleWhen || evaluateCondition(f.visibleWhen, vals))
      .sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  }

  protected controlFor(name: string): FormControl {
    return this.formGroup.controls[name];
  }

  protected optionsFor(name: string): OptionItem[] {
    const def = this.layoutDef();
    const f = def?.fields.find(x => x.name === name);
    if (!f?.options) return [];
    if (f.options.inline) return f.options.inline;
    const all = (this.lookups()[f.options.lookupKey ?? ''] ?? []) as (OptionItem & { parentKey?: string | null })[];
    if (!f.filterBy) return all;
    const parentValue = this.values()[f.filterBy];
    return parentValue == null ? all : all.filter(o => o.parentKey === parentValue);
  }

  protected highlightFor(name: string): FieldHighlight {
    return this.highlights()[name] ?? null;
  }

  protected errorsFor(name: string): string[] {
    const show = this.saveAttempted() || this.touched()[name];
    return [
      ...(show ? this.clientErrors()[name] ?? [] : []),
      ...(this.serverErrors()[name] ?? []),
    ];
  }

  protected formErrors = computed(() => this.serverErrors()['_form'] ?? []);

  protected onInteracted(name: string): void {
    this.touched.update(t => ({ ...t, [name]: true }));
    if (this.highlights()[name])
      this.highlights.update(h => ({ ...h, [name]: null }));
    this.serverErrors.update(e => {
      if (!(name in e)) return e;
      const { [name]: _, ...rest } = e;
      return rest;
    });
  }

  // ---------- reactive behaviors ----------

  private recomputeDerived(def: LayoutDef): void {
    for (const f of def.fields.filter(f => f.computedFrom)) {
      const fn = COMPUTED_FNS[f.computedFrom!.fn];
      if (!fn) continue;
      const inputs = f.computedFrom!.inputs.map(i => this.formGroup.getRawValue()[i]);
      const next = fn(inputs);
      const ctrl = this.formGroup.controls[f.name];
      if (ctrl && ctrl.value !== next) ctrl.setValue(next, { emitEvent: false });
    }
    this.values.set(this.formGroup.getRawValue());
  }

  /** Cascading lookups (spec §4.2): if the child's value leaves the filtered set, clear + amber. Never keep stale values. */
  private applyCascades(def: LayoutDef): void {
    for (const f of def.fields.filter(f => f.filterBy && f.options?.lookupKey)) {
      const ctrl = this.formGroup.controls[f.name];
      const value = ctrl?.value;
      if (value == null) continue;
      const allowed = this.optionsFor(f.name).map(o => o.value);
      const stale = Array.isArray(value) ? !value.every(v => allowed.includes(v)) : !allowed.includes(value);
      if (stale) {
        ctrl.setValue(null, { emitEvent: false });
        this.values.set(this.formGroup.getRawValue());
        delete this.provenance[f.name];
        this.highlights.update(h => ({ ...h, [f.name]: 'cascade-cleared' }));
      }
    }
  }

  private applyEnabledWhen(def: LayoutDef): void {
    const vals = this.formGroup.getRawValue();
    for (const f of def.fields.filter(f => f.enabledWhen)) {
      const ctrl = this.formGroup.controls[f.name];
      const enabled = evaluateCondition(f.enabledWhen!, vals);
      if (enabled && ctrl.disabled) ctrl.enable({ emitEvent: false });
      if (!enabled && ctrl.enabled) ctrl.disable({ emitEvent: false });
    }
  }

  // ---------- client validation (mirrors server §4.5 for UX; server remains authoritative) ----------

  private validateClient(def: LayoutDef): Record<string, string[]> {
    const errors: Record<string, string[]> = {};
    const add = (field: string, msg: string) => (errors[field] ??= []).push(msg);
    const vals = this.values();

    for (const f of def.fields) {
      if (f.visibleWhen && !evaluateCondition(f.visibleWhen, vals)) continue;
      const v = vals[f.name];
      const required = f.requiredWhen ? evaluateCondition(f.requiredWhen, vals) : !!f.required;
      if (required && isEmpty(v)) { add(f.name, `${f.label} is required.`); continue; }
      if (isEmpty(v)) continue;

      if (f.dataType === 'number' && typeof v === 'number') {
        if (typeof f.min === 'number' && v < f.min) add(f.name, `${f.label} must be at least ${f.min}${f.unit ? ' ' + f.unit : ''}.`);
        if (typeof f.max === 'number' && v > f.max) add(f.name, `${f.label} must be at most ${f.max}${f.unit ? ' ' + f.unit : ''}.`);
      }
      if (f.dataType === 'string' && f.pattern && typeof v === 'string' && !new RegExp(f.pattern).test(v))
        add(f.name, `${f.label} does not match the expected format.`);
      if (f.mustBeTrue && v !== true) add(f.name, `${f.label} must be confirmed.`);
      if (f.dataType === 'bpPair' && v && typeof v === 'object') {
        const { sys, dia } = v as { sys: number | null; dia: number | null };
        const b = f.bpBounds ?? { sysMin: 60, sysMax: 260, diaMin: 30, diaMax: 160 };
        if (sys == null || dia == null) add(f.name, 'Enter both systolic and diastolic.');
        else {
          if (sys < b.sysMin || sys > b.sysMax) add(f.name, `Systolic must be ${b.sysMin}-${b.sysMax}.`);
          if (dia < b.diaMin || dia > b.diaMax) add(f.name, `Diastolic must be ${b.diaMin}-${b.diaMax}.`);
          if (dia >= sys) add(f.name, 'Diastolic must be lower than systolic.');
        }
      }
      if (f.dataType === 'date' && typeof v === 'string') {
        const today = new Date().toISOString().slice(0, 10);
        const max = f.max === 'today' ? today : typeof f.max === 'string' ? f.max : null;
        const min = f.min === 'today' ? today : typeof f.min === 'string' ? f.min : null;
        if (max && v > max) add(f.name, `${f.label} must be on or before ${max}.`);
        if (min && v < min) add(f.name, `${f.label} must be on or after ${min}.`);
      }
    }

    for (const rule of def.crossFieldRules ?? []) {
      const refs = referencedFields(rule.condition);
      if (refs.every(r => !isEmpty(vals[r])) && !evaluateCondition(rule.condition, vals))
        refs.forEach(r => add(r, rule.message));
    }
    return errors;
  }

  // ---------- public methods (spec §5) ----------

  /** The one seam shared by voice, photo, and agent input. Values flow through the same validation as typing. */
  populate(values: PartialValues, source: PopulateSource, confidences?: Record<string, number>): PopulateResult {
    const def = this.layoutDef();
    const result: PopulateResult = { applied: [], skipped: [] };
    if (!def) return result;
    const vals = this.values();

    this.populating = true;
    try {
      for (const [name, incoming] of Object.entries(values)) {
        const f = def.fields.find(x => x.name === name);
        if (!f) { result.skipped.push({ field: name, reason: 'unknownField' }); continue; }
        if (f.computedFrom) { result.skipped.push({ field: name, reason: 'readOnly' }); continue; }
        if (f.visibleWhen && !evaluateCondition(f.visibleWhen, vals)) {
          result.skipped.push({ field: name, reason: 'hidden' });
          continue;
        }

        let value = incoming;
        if (value != null && f.options) {
          const allowed = this.optionsFor(name).map(o => o.value);
          if (Array.isArray(value)) {
            const ok = value.filter(v => allowed.includes(v as string));
            if (!ok.length) { result.skipped.push({ field: name, reason: 'invalidOption' }); continue; }
            // Arrays merge (union) on populate — dictating allergies one at a time must not lose earlier ones.
            const current = Array.isArray(vals[name]) ? vals[name] as string[] : [];
            value = [...new Set([...current, ...ok])];
          } else if (!allowed.includes(value as string)) {
            result.skipped.push({ field: name, reason: 'invalidOption' });
            continue;
          }
        }

        const ctrl = this.formGroup.controls[name];
        ctrl.setValue(value);
        ctrl.markAsDirty();
        result.applied.push(name);

        if (value == null) {
          delete this.provenance[name];
          this.highlights.update(h => ({ ...h, [name]: null }));
        } else {
          const confidence = confidences?.[name];
          this.provenance[name] = { source, confidence, ts: new Date().toISOString() };
          const state: FieldHighlight = source === 'manual' ? null
            : confidence != null && confidence < 0.85 ? 'ai-review' : 'ai-confident';
          this.highlights.update(h => ({ ...h, [name]: state }));
          if (state === 'ai-confident')
            setTimeout(() => this.highlights.update(h => h[name] === 'ai-confident' ? { ...h, [name]: null } : h), 1400);
        }
      }
    } finally {
      this.populating = false;
    }
    this.populated.emit(result);
    return result;
  }

  async save(): Promise<SaveResult> {
    const def = this.layoutDef();
    if (!def) return { ok: false, errors: { _form: ['No layout loaded.'] } };
    this.saveAttempted.set(true);
    const clientErrors = this.validateClient(def);
    this.clientErrors.set(clientErrors);
    if (Object.keys(clientErrors).length) return { ok: false, errors: clientErrors };

    const vals = this.values();
    const visible: PartialValues = {};
    for (const f of def.fields) {
      if (f.visibleWhen && !evaluateCondition(f.visibleWhen, vals)) continue;
      if (!isEmpty(vals[f.name])) visible[f.name] = vals[f.name];
    }
    const provenance = Object.fromEntries(Object.entries(this.provenance).filter(([k]) => k in visible));

    const result = await this.data.saveRecord({
      id: this.recordId,
      layoutKey: this.layoutKey(),
      layoutVersion: this.resolvedVersion(),
      values: visible,
      provenance,
      context: this.context(),
    });

    if (result.ok && result.recordId) {
      this.serverErrors.set({});
      this.formGroup.markAsPristine();
      this.dirtyChange.emit(false);
      this.saved.emit({ recordId: result.recordId });
    } else if (result.errors) {
      this.serverErrors.set(result.errors);
    }
    return result;
  }

  /** Load an existing record. The host must render this component at the record's stamped version. */
  loadRecord(record: FormRecord): void {
    this.recordId = record.id;
    this.populating = true;
    try {
      for (const [name, value] of Object.entries(record.values)) {
        const ctrl = this.formGroup.controls[name];
        if (ctrl) ctrl.setValue(value);
      }
      this.provenance = { ...record.provenance };
    } finally {
      this.populating = false;
    }
    this.formGroup.markAsPristine();
  }

  getFormGroup(): FormGroup {
    return this.formGroup;
  }

  reset(): void {
    this.formGroup.reset();
    this.provenance = {};
    this.recordId = crypto.randomUUID();
    this.highlights.set({});
    this.clientErrors.set({});
    this.serverErrors.set({});
    this.touched.set({});
    this.saveAttempted.set(false);
  }

  get currentVersion(): number {
    return this.resolvedVersion();
  }
}
