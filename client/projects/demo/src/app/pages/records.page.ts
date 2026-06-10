import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormRecord, SynFormDataService } from 'syn-form';

/** Saved records: list + inspect values/provenance, stamped layout version visible. */
@Component({
  selector: 'app-records',
  standalone: true,
  imports: [CommonModule],
  template: `
    <h1>Saved Records</h1>
    <table class="grid">
      <thead><tr><th>Created</th><th>Layout</th><th>Version</th><th>Patient</th><th></th></tr></thead>
      <tbody>
        @for (r of records(); track r.id) {
          <tr>
            <td>{{ r.createdAt | date:'medium' }}</td>
            <td>{{ r.layoutKey }}</td>
            <td><span class="badge">v{{ r.layoutVersion }}</span></td>
            <td>{{ patientName(r) }}</td>
            <td class="row-actions">
              <button class="btn sm" (click)="selected.set(selected() === r ? null : r)">
                {{ selected() === r ? 'Hide' : 'View' }}
              </button>
              <button class="btn sm del" (click)="remove(r)">Delete</button>
            </td>
          </tr>
          @if (selected() === r) {
            <tr><td colspan="5">
              <div class="detail">
                <div><h3>Values</h3><pre>{{ r.values | json }}</pre></div>
                <div><h3>Provenance</h3><pre>{{ r.provenance | json }}</pre></div>
              </div>
            </td></tr>
          }
        } @empty {
          <tr><td colspan="5" class="empty">No records saved yet.</td></tr>
        }
      </tbody>
    </table>
  `,
  styles: [`
    h1 { font-size: 22px; margin: 0 0 16px; }
    .grid { width: 100%; border-collapse: collapse; font-size: 14px; }
    .grid th { text-align: left; font-size: 12px; text-transform: uppercase; letter-spacing: .05em; color: #6b7280; padding: 8px 10px; border-bottom: 2px solid #e5e7eb; }
    .grid td { padding: 9px 10px; border-bottom: 1px solid #f3f4f6; }
    .badge { background: #eff6ff; color: #1e40af; border-radius: 6px; padding: 2px 10px; font-size: 12.5px; font-weight: 700; }
    .row-actions { display: flex; gap: 8px; }
    .btn { border: 1.5px solid #d1d5db; background: #fff; border-radius: 8px; padding: 4px 12px; font: inherit; font-size: 13px; cursor: pointer; }
    .btn.del { color: #dc2626; }
    .detail { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; background: #f9fafb; border-radius: 10px; padding: 12px 16px; }
    .detail h3 { font-size: 12.5px; text-transform: uppercase; color: #6b7280; margin: 0 0 6px; }
    .detail pre { margin: 0; font-size: 12px; overflow-x: auto; }
    .empty { color: #9ca3af; text-align: center; padding: 24px; }
  `],
})
export class RecordsPage implements OnInit {
  private data = inject(SynFormDataService);
  records = signal<FormRecord[]>([]);
  selected = signal<FormRecord | null>(null);

  async ngOnInit(): Promise<void> {
    this.records.set(await this.data.listRecords());
  }

  patientName(r: FormRecord): string {
    return typeof r.values['patientName'] === 'string' ? r.values['patientName'] as string : '—';
  }

  async remove(r: FormRecord): Promise<void> {
    if (!confirm('Delete this record?')) return;
    await this.data.deleteRecord(r.id);
    this.records.set(await this.data.listRecords());
  }
}
