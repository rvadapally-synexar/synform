import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ExtractResponse, FormRecord, LayoutEnvelope, LayoutSummary, LookupItem,
  PartialValues, ProvenanceEntry, SaveResult,
} from './types';

/** API base URL, e.g. http://localhost:5266. Provided by the host application. */
export const SYN_FORM_API_BASE = new InjectionToken<string>('SYN_FORM_API_BASE');

/**
 * The single data-access seam for the library. Components never touch HttpClient directly,
 * so a caching/outbox (offline) implementation can replace this without touching components.
 */
@Injectable({ providedIn: 'root' })
export class SynFormDataService {
  private http = inject(HttpClient);
  private base = inject(SYN_FORM_API_BASE);

  // --- layouts ---
  listLayouts(): Promise<LayoutSummary[]> {
    return firstValueFrom(this.http.get<LayoutSummary[]>(`${this.base}/api/layouts`));
  }
  getLayout(key: string, version: number | 'latestPublished' | 'draft'): Promise<LayoutEnvelope> {
    const url = version === 'latestPublished' ? `${this.base}/api/layouts/${key}`
      : version === 'draft' ? `${this.base}/api/layouts/${key}/draft`
      : `${this.base}/api/layouts/${key}/versions/${version}`;
    return firstValueFrom(this.http.get<LayoutEnvelope>(url));
  }
  createLayout(layoutKey: string, title: string): Promise<LayoutEnvelope> {
    return firstValueFrom(this.http.post<LayoutEnvelope>(`${this.base}/api/layouts`, { layoutKey, title }));
  }
  createDraft(key: string): Promise<LayoutEnvelope> {
    return firstValueFrom(this.http.post<LayoutEnvelope>(`${this.base}/api/layouts/${key}/draft`, {}));
  }
  saveDraft(key: string, json: unknown, expectedUpdatedAt?: string): Promise<LayoutEnvelope> {
    return firstValueFrom(this.http.put<LayoutEnvelope>(`${this.base}/api/layouts/${key}/draft`, { json, expectedUpdatedAt }));
  }
  discardDraft(key: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/api/layouts/${key}/draft`));
  }
  cloneLayout(key: string, newLayoutKey: string, title?: string): Promise<LayoutEnvelope> {
    return firstValueFrom(this.http.post<LayoutEnvelope>(`${this.base}/api/layouts/${key}/clone`, { newLayoutKey, title }));
  }
  async publish(key: string): Promise<{ ok: boolean; errors?: string[]; layout?: LayoutEnvelope }> {
    try {
      const layout = await firstValueFrom(this.http.post<LayoutEnvelope>(`${this.base}/api/layouts/${key}/publish`, {}));
      return { ok: true, layout };
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 422) return { ok: false, errors: e.error?.errors ?? ['Publish failed'] };
      throw e;
    }
  }
  getSchema(key: string, version: number | 'draft' | 'latestPublished'): Promise<unknown> {
    return firstValueFrom(this.http.get(`${this.base}/api/layouts/${key}/schema`, { params: { version: String(version) } }));
  }

  // --- lookups ---
  getLookup(key: string, parent?: string, includeInactive = false): Promise<LookupItem[]> {
    const params: Record<string, string> = {};
    if (parent != null) params['parent'] = parent;
    if (includeInactive) params['includeInactive'] = 'true';
    return firstValueFrom(this.http.get<LookupItem[]>(`${this.base}/api/lookups/${key}`, { params }));
  }
  listLookupKeys(): Promise<string[]> {
    return firstValueFrom(this.http.get<string[]>(`${this.base}/api/lookups`));
  }
  createLookupItem(item: Partial<LookupItem>): Promise<LookupItem> {
    return firstValueFrom(this.http.post<LookupItem>(`${this.base}/api/lookups`, item));
  }
  updateLookupItem(id: string, item: Partial<LookupItem>): Promise<LookupItem> {
    return firstValueFrom(this.http.put<LookupItem>(`${this.base}/api/lookups/${id}`, item));
  }
  deactivateLookupItem(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/api/lookups/${id}`));
  }

  // --- records ---
  async saveRecord(body: {
    id?: string; layoutKey: string; layoutVersion: number;
    values: PartialValues; provenance: Record<string, ProvenanceEntry>; context?: unknown;
  }): Promise<SaveResult> {
    try {
      const r = await firstValueFrom(this.http.post<{ recordId: string }>(`${this.base}/api/records`, body));
      return { ok: true, recordId: r.recordId };
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 422) return { ok: false, errors: e.error?.errors ?? {} };
      throw e;
    }
  }
  listRecords(layoutKey?: string): Promise<FormRecord[]> {
    const params: Record<string, string> = layoutKey ? { layoutKey } : {};
    return firstValueFrom(this.http.get<FormRecord[]>(`${this.base}/api/records`, { params }));
  }
  getRecord(id: string): Promise<FormRecord> {
    return firstValueFrom(this.http.get<FormRecord>(`${this.base}/api/records/${id}`));
  }
  deleteRecord(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.base}/api/records/${id}`));
  }

  // --- extraction (one seam, all modalities) ---
  extract(body: {
    layoutKey: string; layoutVersion?: number;
    inputType: 'text' | 'transcript' | 'image'; text?: string; imageBase64?: string;
  }): Promise<ExtractResponse> {
    return firstValueFrom(this.http.post<ExtractResponse>(`${this.base}/api/extract`, body));
  }
  sttToken(): Promise<{ access_token: string; expires_in: number }> {
    return firstValueFrom(this.http.post<{ access_token: string; expires_in: number }>(`${this.base}/api/stt/token`, {}));
  }
  sttEngine(): Promise<{ engine: 'whisper' | 'vibevoice' | 'deepgram' | 'none' }> {
    return firstValueFrom(this.http.get<{ engine: 'whisper' | 'vibevoice' | 'deepgram' | 'none' }>(`${this.base}/api/stt/engine`));
  }
  transcribe(audio: Blob, layoutKey?: string): Promise<string> {
    const form = new FormData();
    form.append('audio', audio, 'audio.webm');
    if (layoutKey) form.append('layoutKey', layoutKey);
    return firstValueFrom(this.http.post<{ text: string }>(`${this.base}/api/stt/transcribe`, form))
      .then(r => r.text);
  }
}
