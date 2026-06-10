import { CommonModule } from '@angular/common';
import { Component, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SynFormComponent, SynFormDataService, SynFormGroupDirective, SynVoicePanelComponent,
} from 'syn-form';

/**
 * Host page for the Pre-Anesthesia Assessment. Demonstrates the architecture decision:
 * field *positioning* is design-time host markup (the synFormGroup slots below);
 * field *metadata* is runtime layout JSON from the Maintainer.
 */
@Component({
  selector: 'app-demo-form',
  standalone: true,
  imports: [CommonModule, FormsModule, SynFormComponent, SynFormGroupDirective, SynVoicePanelComponent],
  template: `
    <header class="page-head">
      <h1>Pre-Anesthesia Assessment</h1>
      <div class="actions">
        <span class="dirty" [class.show]="dirty()">● unsaved changes</span>
        <button class="btn ghost" (click)="form.reset()">Reset</button>
        <button class="btn primary" (click)="save()">Save</button>
      </div>
    </header>

    @if (savedId()) { <div class="banner ok">Saved — record {{ savedId() }}</div> }

    <syn-form #form layoutKey="pre-anesthesia-assessment" (saved)="savedId.set($event.recordId)"
              (dirtyChange)="dirty.set($event)">
      <div class="form-grid">
        <section class="col">
          <h2>Patient</h2>
          <div synFormGroup="patient"></div>
          <h2>Assessment</h2>
          <div synFormGroup="assessment"></div>
        </section>
        <section class="col">
          <h2>Airway</h2>
          <div synFormGroup="airway"></div>
          <h2>Procedure</h2>
          <div synFormGroup="procedure"></div>
        </section>
      </div>
    </syn-form>

    <syn-voice-panel [synForm]="form" layoutKey="pre-anesthesia-assessment" />

    <details class="sim">
      <summary>Agent / transcript simulator (works without a microphone)</summary>
      <div class="sim-body">
        <textarea [(ngModel)]="simText" rows="2"
          placeholder='Try: "BP one twenty over eighty, weight 82 kilos, ASA three, smoker yes, pack years 10"'></textarea>
        <div class="sim-actions">
          <button class="btn" (click)="simulate()" [disabled]="simBusy()">Extract → populate()</button>
          <label class="btn ghost">
            Photo → populate()
            <input type="file" accept="image/*" hidden (change)="photo($event)" />
          </label>
        </div>
        @if (simResult()) { <pre class="sim-result">{{ simResult() }}</pre> }
      </div>
    </details>
  `,
  styles: [`
    .page-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
    h1 { font-size: 22px; margin: 0; }
    h2 { font-size: 14px; text-transform: uppercase; letter-spacing: .06em; color: #6b7280; margin: 18px 0 10px; }
    .actions { display: flex; gap: 10px; align-items: center; }
    .dirty { color: #b45309; font-size: 13px; opacity: 0; transition: opacity .2s; }
    .dirty.show { opacity: 1; }
    .btn {
      border: 1.5px solid #d1d5db; background: #fff; border-radius: 8px;
      padding: 8px 18px; font: inherit; font-size: 14px; cursor: pointer;
    }
    .btn.primary { background: #2563eb; border-color: #2563eb; color: #fff; font-weight: 600; }
    .btn.ghost { background: transparent; }
    .banner.ok {
      background: #f0fdf4; border: 1px solid #86efac; color: #166534;
      border-radius: 8px; padding: 10px 14px; margin-bottom: 12px; font-size: 14px;
    }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 36px; }
    @media (max-width: 900px) { .form-grid { grid-template-columns: 1fr; } }
    .sim { margin-top: 28px; border-top: 1px dashed #d1d5db; padding-top: 12px; color: #374151; }
    .sim summary { cursor: pointer; font-size: 14px; }
    .sim-body { display: flex; flex-direction: column; gap: 10px; margin-top: 10px; }
    .sim-body textarea {
      width: 100%; box-sizing: border-box; font: inherit; font-size: 14px;
      border: 1.5px solid #d1d5db; border-radius: 8px; padding: 10px;
    }
    .sim-actions { display: flex; gap: 10px; }
    .sim-result { background: #f9fafb; border-radius: 8px; padding: 10px; font-size: 12.5px; overflow-x: auto; }
  `],
})
export class DemoFormPage {
  form = viewChild.required(SynFormComponent);
  private data = inject(SynFormDataService);

  dirty = signal(false);
  savedId = signal<string | null>(null);
  simText = '';
  simBusy = signal(false);
  simResult = signal('');

  async save(): Promise<void> {
    this.savedId.set(null);
    await this.form().save();
  }

  /** Same path as voice: /extract → populate(). Proves the one-seam design without audio hardware. */
  async simulate(): Promise<void> {
    if (!this.simText.trim()) return;
    this.simBusy.set(true);
    try {
      const r = await this.data.extract({
        layoutKey: 'pre-anesthesia-assessment',
        layoutVersion: this.form().currentVersion,
        inputType: 'transcript',
        text: this.simText,
      });
      const populate = this.form().populate(r.values, 'agent', r.confidences);
      this.simResult.set(JSON.stringify({ layer: r.layer, applied: populate.applied, skipped: [...populate.skipped, ...r.skipped] }, null, 2));
    } catch (e) {
      this.simResult.set('Extraction failed: ' + (e instanceof Error ? e.message : String(e)));
    } finally {
      this.simBusy.set(false);
    }
  }

  async photo(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    this.simBusy.set(true);
    try {
      const base64 = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve((reader.result as string).split(',')[1]);
        reader.onerror = reject;
        reader.readAsDataURL(file);
      });
      const r = await this.data.extract({
        layoutKey: 'pre-anesthesia-assessment',
        layoutVersion: this.form().currentVersion,
        inputType: 'image',
        imageBase64: base64,
      });
      const populate = this.form().populate(r.values, 'photo', r.confidences);
      this.simResult.set(JSON.stringify({ layer: r.layer, applied: populate.applied, skipped: populate.skipped }, null, 2));
    } catch (e) {
      this.simResult.set('Photo extraction failed (is an OpenAI key configured?): ' + (e instanceof Error ? e.message : String(e)));
    } finally {
      this.simBusy.set(false);
    }
  }
}
