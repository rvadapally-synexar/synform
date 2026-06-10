import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { FieldDef, FieldHighlight, OptionItem } from './types';

/**
 * Renders one field. Variant selection (desktop vs touch) is decided here from the
 * injected coarse-pointer state — the layout JSON never encodes surface information (spec §4.1).
 */
@Component({
  selector: 'syn-field',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="syn-field"
         [class.coarse]="coarse()"
         [class.compact]="density() === 'compact'"
         [class.hl-confident]="highlight() === 'ai-confident'"
         [class.hl-review]="highlight() === 'ai-review' || highlight() === 'cascade-cleared'"
         [class.invalid]="showErrors()"
         (focusin)="interacted.emit()">
      <label class="syn-label" [attr.for]="id">
        {{ field().label }}
        @if (isRequired()) { <span class="req" aria-hidden="true">*</span> }
        @if (highlight() === 'ai-review') { <span class="dot review" title="AI-filled — needs review"></span> }
        @if (highlight() === 'cascade-cleared') { <span class="dot review" title="Cleared by a dependent change — needs review"></span> }
        @if (field().unit) { <span class="unit">({{ field().unit }})</span> }
      </label>

      @switch (variant()) {
        @case ('text') {
          <input class="syn-input" [id]="id" type="text" [formControl]="control()" />
        }
        @case ('textarea') {
          <textarea class="syn-input" [id]="id" rows="3" [formControl]="control()"></textarea>
        }
        @case ('number') {
          <input class="syn-input num" [id]="id" type="number" [formControl]="control()"
                 [readonly]="!!field().computedFrom" [min]="numMin()" [max]="numMax()" />
        }
        @case ('stepper') {
          <div class="stepper" role="group" [attr.aria-label]="field().label">
            <button type="button" (click)="step(-1)" [disabled]="control().disabled" aria-label="decrease">−</button>
            <input class="syn-input num" [id]="id" type="number" inputmode="decimal" [formControl]="control()"
                   [readonly]="!!field().computedFrom" />
            <button type="button" (click)="step(1)" [disabled]="control().disabled" aria-label="increase">+</button>
          </div>
        }
        @case ('select') {
          <select class="syn-input" [id]="id" [formControl]="control()">
            <option [ngValue]="null"></option>
            @for (o of options(); track o.value) { <option [ngValue]="o.value">{{ o.label }}</option> }
          </select>
        }
        @case ('segmented') {
          <div class="segmented" role="radiogroup" [attr.aria-label]="field().label">
            @for (o of options(); track o.value) {
              <button type="button" role="radio" [attr.aria-checked]="control().value === o.value"
                      [class.on]="control().value === o.value"
                      [disabled]="control().disabled"
                      (click)="setValue(control().value === o.value ? null : o.value)">{{ o.label }}</button>
            }
          </div>
        }
        @case ('sheet') {
          <button type="button" class="syn-input sheet-trigger" [id]="id" [disabled]="control().disabled"
                  (click)="sheetOpen = true">
            {{ selectedLabel() || 'Select…' }}
          </button>
          @if (sheetOpen) {
            <div class="sheet-backdrop" (click)="sheetOpen = false">
              <div class="sheet" role="listbox" [attr.aria-label]="field().label" (click)="$event.stopPropagation()">
                <header>{{ field().label }}</header>
                @for (o of options(); track o.value) {
                  <button type="button" role="option" [attr.aria-selected]="control().value === o.value"
                          [class.on]="control().value === o.value"
                          (click)="setValue(o.value); sheetOpen = false">{{ o.label }}</button>
                }
                <button type="button" class="sheet-cancel" (click)="sheetOpen = false">Cancel</button>
              </div>
            </div>
          }
        }
        @case ('radio') {
          <div class="radio-group" role="radiogroup" [attr.aria-label]="field().label">
            @for (o of options(); track o.value) {
              <label class="radio-item">
                <input type="radio" [name]="id" [value]="o.value"
                       [checked]="control().value === o.value"
                       [disabled]="control().disabled"
                       (change)="setValue(o.value)" />
                {{ o.label }}
              </label>
            }
          </div>
        }
        @case ('listbox') {
          <select class="syn-input listbox" [id]="id" multiple [size]="listSize()"
                  [disabled]="control().disabled"
                  (change)="onMultiSelect($event)">
            @for (o of options(); track o.value) {
              <option [value]="o.value" [selected]="isSelected(o.value)">{{ o.label }}</option>
            }
          </select>
        }
        @case ('chips') {
          <div class="chips" role="group" [attr.aria-label]="field().label">
            @for (o of options(); track o.value) {
              <button type="button" class="chip" [class.on]="isSelected(o.value)"
                      [attr.aria-pressed]="isSelected(o.value)"
                      [disabled]="control().disabled"
                      (click)="toggleArrayValue(o.value)">{{ o.label }}</button>
            }
          </div>
        }
        @case ('checkList') {
          <div class="check-list" role="group" [attr.aria-label]="field().label">
            @for (o of options(); track o.value) {
              <label class="radio-item">
                <input type="checkbox" [checked]="isSelected(o.value)"
                       [disabled]="control().disabled"
                       (change)="toggleArrayValue(o.value)" />
                {{ o.label }}
              </label>
            }
          </div>
        }
        @case ('checkbox') {
          <label class="radio-item">
            <input [id]="id" type="checkbox" [checked]="control().value === true"
                   [disabled]="control().disabled"
                   (change)="setValue(!(control().value === true))" />
            <span>{{ field().label }}</span>
          </label>
        }
        @case ('toggle') {
          <button type="button" class="toggle" [id]="id" role="switch"
                  [attr.aria-checked]="control().value === true"
                  [class.on]="control().value === true"
                  [disabled]="control().disabled"
                  (click)="setValue(!(control().value === true))">
            <span class="knob"></span>
          </button>
        }
        @case ('date') {
          <input class="syn-input" [id]="id" type="date" [formControl]="control()" [max]="dateMax()" [min]="dateMin()" />
        }
        @case ('time') {
          <input class="syn-input" [id]="id" type="time" [formControl]="control()" />
        }
        @case ('bp') {
          <div class="bp" role="group" [attr.aria-label]="field().label">
            @if (coarse()) {
              <div class="stepper"><button type="button" (click)="stepBp('sys', -1)" aria-label="decrease systolic">−</button>
                <input type="number" inputmode="numeric" aria-label="systolic" [value]="bpPart('sys')" (input)="setBpPart('sys', $event)" />
                <button type="button" (click)="stepBp('sys', 1)" aria-label="increase systolic">+</button></div>
              <span class="bp-sep">/</span>
              <div class="stepper"><button type="button" (click)="stepBp('dia', -1)" aria-label="decrease diastolic">−</button>
                <input type="number" inputmode="numeric" aria-label="diastolic" [value]="bpPart('dia')" (input)="setBpPart('dia', $event)" />
                <button type="button" (click)="stepBp('dia', 1)" aria-label="increase diastolic">+</button></div>
            } @else {
              <input class="syn-input num" type="number" placeholder="120" aria-label="systolic"
                     [value]="bpPart('sys')" (input)="setBpPart('sys', $event)" />
              <span class="bp-sep">/</span>
              <input class="syn-input num" type="number" placeholder="80" aria-label="diastolic"
                     [value]="bpPart('dia')" (input)="setBpPart('dia', $event)" />
            }
          </div>
        }
      }

      @for (e of errors(); track e) { <div class="syn-error" role="alert">{{ e }}</div> }
    </div>
  `,
  styleUrls: ['./field.component.scss'],
})
export class SynFieldComponent {
  field = input.required<FieldDef>();
  control = input.required<FormControl>();
  options = input<OptionItem[]>([]);
  coarse = input(false);
  density = input<'comfortable' | 'compact'>('comfortable');
  highlight = input<FieldHighlight>(null);
  errors = input<string[]>([]);
  interacted = output<void>();

  sheetOpen = false;
  readonly id = `syn-${Math.random().toString(36).slice(2, 9)}`;

  /** Variant table from spec §4.1, decided by pointer + option count. */
  variant = computed<string>(() => {
    const f = this.field();
    const coarse = this.coarse();
    const n = this.options().length;
    switch (f.controlType) {
      case 'text': return 'text';
      case 'textarea': return 'textarea';
      case 'number': return coarse && !f.computedFrom ? 'stepper' : 'number';
      case 'dropdown': return coarse ? (n <= 5 ? 'segmented' : 'sheet') : 'select';
      case 'multiselect': return coarse ? 'chips' : 'listbox';
      case 'radio': return coarse ? 'segmented' : 'radio';
      case 'checkbox': return coarse ? 'toggle' : 'checkbox';
      case 'checkboxGroup': return coarse ? 'chips' : 'checkList';
      case 'date': return 'date';
      case 'time': return 'time';
      case 'bpPair': return 'bp';
      default: return 'text';
    }
  });

  isRequired = computed(() => !!this.field().required || !!this.field().mustBeTrue);
  numMin = computed<number | null>(() => {
    const min = this.field().min;
    return typeof min === 'number' ? min : null;
  });
  numMax = computed<number | null>(() => {
    const max = this.field().max;
    return typeof max === 'number' ? max : null;
  });
  dateMin = computed(() => this.dateBound(this.field().min));
  dateMax = computed(() => this.dateBound(this.field().max));
  listSize = computed(() => Math.min(6, Math.max(3, this.options().length)));
  selectedLabel = computed(() => this.options().find(o => o.value === this.control().value)?.label ?? '');
  showErrors = computed(() => this.errors().length > 0);

  private dateBound(bound: number | string | null | undefined): string | null {
    if (bound === 'today') return new Date().toISOString().slice(0, 10);
    return typeof bound === 'string' ? bound : null;
  }

  setValue(v: unknown): void {
    this.control().setValue(v);
    this.control().markAsDirty();
    this.interacted.emit();
  }

  isSelected(value: string): boolean {
    const v = this.control().value;
    return Array.isArray(v) && v.includes(value);
  }

  toggleArrayValue(value: string): void {
    const current: string[] = Array.isArray(this.control().value) ? [...this.control().value] : [];
    const idx = current.indexOf(value);
    if (idx >= 0) current.splice(idx, 1); else current.push(value);
    this.setValue(current.length ? current : null);
  }

  onMultiSelect(event: Event): void {
    const selected = Array.from((event.target as HTMLSelectElement).selectedOptions).map(o => o.value);
    this.setValue(selected.length ? selected : null);
  }

  step(direction: number): void {
    const current = Number(this.control().value ?? 0) || 0;
    let next = current + direction;
    const min = this.numMin();
    const max = this.numMax();
    if (min != null && next < min) next = min;
    if (max != null && next > max) next = max;
    this.setValue(next);
  }

  bpPart(part: 'sys' | 'dia'): string {
    const v = this.control().value;
    return v && typeof v === 'object' && v[part] != null ? String(v[part]) : '';
  }

  setBpPart(part: 'sys' | 'dia', event: Event): void {
    const raw = (event.target as HTMLInputElement).value;
    const num = raw === '' ? null : Number(raw);
    const current = this.control().value && typeof this.control().value === 'object'
      ? { ...this.control().value } : { sys: null, dia: null };
    current[part] = num;
    this.setValue(current.sys == null && current.dia == null ? null : current);
  }

  stepBp(part: 'sys' | 'dia', direction: number): void {
    const current = this.control().value && typeof this.control().value === 'object'
      ? { ...this.control().value } : { sys: null, dia: null };
    const defaults = { sys: 120, dia: 80 };
    current[part] = (current[part] ?? defaults[part]) + direction;
    this.setValue(current);
  }
}
