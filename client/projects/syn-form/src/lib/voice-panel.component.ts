import { CommonModule } from '@angular/common';
import { Overlay, OverlayModule, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, TemplateRef,
  ViewContainerRef, inject, input, signal, viewChild,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { AudioSource, VibeVoiceAudioSource } from './audio-source';
import { SttEngine, SynFormDataService } from './data.service';
import { PointerModeService } from './pointer';
import { SynFormComponent } from './syn-form.component';

/**
 * <syn-voice-panel> — floating dictation panel (spec §8). CDK overlay: bottom-right on
 * desktop, bottom-center (thumb-reachable) on touch.
 *
 * EVERY engine is push-to-talk batch: record the whole dictation → one transcription →
 * one /extract over the complete transcript → one populate. Deepgram streaming was
 * tried and rejected (2026-06-12): per-utterance extraction populated fields from
 * fragments out of context, and live mid-dictation population had no user value.
 * The engine dropdown lets the user A/B Whisper vs Deepgram on the same workflow.
 */
@Component({
  selector: 'syn-voice-panel',
  standalone: true,
  imports: [CommonModule, OverlayModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #panel>
      <div class="voice-panel" [class.listening]="listening()">
        <div class="main-row">
          @if (!listening()) {
            <button type="button" class="mic" (click)="toggle()" aria-label="Start dictation">🎤</button>
            <div class="status">
              @if (error()) { <span class="err">{{ error() }}</span> }
              @else if (processing()) { <span class="spin" aria-hidden="true"></span> Transcribing ({{ engineLabel(engine()) }})… }
              @else if (summary()) { <span class="summary">{{ summary() }}</span> }
              @else { Tap the mic and dictate }
            </div>
            @if (available().length > 1) {
              <!-- [selected] per option, NOT [value] on the select: the select re-renders
                   when recording ends, and a value set before options exist silently
                   falls back to the first option (displayed "whisper", used deepgram). -->
              <select class="engine-sel" (change)="selectEngine($event)"
                      aria-label="Speech engine" title="Speech-to-text engine">
                @for (e of available(); track e) {
                  <option [value]="e" [selected]="e === engine()">{{ engineLabel(e) }}</option>
                }
              </select>
            }
          } @else {
            <span class="engine-badge">{{ engineLabel(engine()) }}</span>
            <!-- Recording: live input-level waveform (client-side, engine-independent) + ✕/✓ -->
            <canvas #wave class="wave" width="260" height="36" aria-hidden="true"></canvas>
            @if (transcript()) { <span class="live-partial">{{ transcript() }}</span> }
            <button type="button" class="ctl cancel" (click)="cancelDictation()"
                    aria-label="Cancel dictation (Esc)" title="Cancel (Esc)">✕</button>
            <button type="button" class="ctl ok" (click)="toggle()"
                    aria-label="Finish and transcribe" title="Finish">✓</button>
          }
        </div>
        <!-- What did I just say? The transcript log keeps every utterance visible. -->
        @if (log().length) {
          <div class="log" role="log" aria-label="Transcript">
            @for (entry of log(); track $index) {
              <div class="log-entry">“{{ entry }}”</div>
            }
            <button type="button" class="log-clear" (click)="log.set([])" aria-label="Clear transcript">clear</button>
          </div>
        }
      </div>
    </ng-template>
  `,
  styles: [`
    .voice-panel {
      display: flex; flex-direction: column; gap: 8px;
      background: #1f2937; color: #f9fafb; border-radius: 24px;
      padding: 8px 20px 8px 8px; box-shadow: 0 8px 24px rgb(0 0 0 / .35);
      max-width: min(520px, 92vw); font-size: 14px;
    }
    .main-row { display: flex; align-items: center; gap: 12px; min-height: 48px; }
    .wave { border-radius: 8px; flex-shrink: 1; min-width: 0; }
    .live-partial { font-size: 12.5px; color: #d1d5db; font-style: italic; max-width: 140px;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .ctl {
      width: 40px; height: 40px; border-radius: 50%; border: none; cursor: pointer;
      font-size: 17px; flex-shrink: 0; display: grid; place-items: center;
    }
    .engine-sel {
      background: rgb(255 255 255 / .08); color: #d1d5db; border: 1px solid rgb(255 255 255 / .18);
      border-radius: 8px; padding: 5px 8px; font-size: 12px; cursor: pointer; flex-shrink: 0;
    }
    .engine-sel option { background: #1f2937; }
    .engine-badge {
      background: rgb(255 255 255 / .1); color: #d1d5db; border-radius: 8px;
      padding: 3px 8px; font-size: 11px; flex-shrink: 0;
    }
    .ctl.cancel { background: rgb(255 255 255 / .12); color: #f3f4f6; }
    .ctl.cancel:hover { background: rgb(220 38 38 / .55); }
    .ctl.ok { background: #16a34a; color: #fff; font-weight: 700; }
    .ctl.ok:hover { background: #15803d; }
    .log {
      position: relative; margin: 0 4px 6px 12px; padding: 8px 10px;
      background: rgb(255 255 255 / .07); border-radius: 12px;
      max-height: 140px; overflow-y: auto; font-size: 13px; line-height: 1.45;
    }
    .log-entry { color: #e5e7eb; padding: 2px 0; }
    .log-entry + .log-entry { border-top: 1px dashed rgb(255 255 255 / .12); margin-top: 4px; padding-top: 6px; }
    .log-clear {
      position: sticky; bottom: 0; float: right; border: none; background: transparent;
      color: #9ca3af; font-size: 11.5px; cursor: pointer; text-decoration: underline; padding: 2px 0 0;
    }
    .mic {
      width: 48px; height: 48px; border-radius: 50%; border: none; cursor: pointer;
      background: #2563eb; color: #fff; font-size: 20px; flex-shrink: 0;
    }
    .listening .mic { background: #dc2626; animation: pulse 1.4s infinite; }
    @keyframes pulse { 50% { box-shadow: 0 0 0 10px rgb(220 38 38 / .25); } }
    .status { min-width: 160px; overflow: hidden; }
    .transcript { font-style: italic; color: #d1d5db; }
    .summary { color: #86efac; }
    .err { color: #fca5a5; }
    .spin {
      display: inline-block; width: 14px; height: 14px; border-radius: 50%;
      border: 2px solid #9ca3af; border-top-color: #f9fafb;
      animation: rot .8s linear infinite; vertical-align: -2px; margin-right: 6px;
    }
    @keyframes rot { to { transform: rotate(360deg); } }
  `],
})
export class SynVoicePanelComponent implements OnInit, OnDestroy {
  /** The form this panel populates. */
  synForm = input.required<SynFormComponent>();
  layoutKey = input.required<string>();
  layoutVersion = input<number | undefined>(undefined);

  listening = signal(false);
  processing = signal(false);
  transcript = signal('');
  summary = signal('');
  error = signal('');
  /** User-selected STT engine (dropdown); defaults to the server's resolution. */
  engine = signal<SttEngine>('none');
  /** Engines the server has credentials for — the dropdown renders when there are 2+. */
  available = signal<SttEngine[]>([]);
  /** Every transcribed utterance, newest last — the user sees exactly what the ASR heard. */
  log = signal<string[]>([]);

  private data = inject(SynFormDataService);
  private pointer = inject(PointerModeService);
  private overlay = inject(Overlay);
  private vcr = inject(ViewContainerRef);
  private panelTpl = viewChild.required<TemplateRef<unknown>>('panel');

  private overlayRef?: OverlayRef;
  private source?: AudioSource;
  private sub?: Subscription;

  private waveCanvas = viewChild<ElementRef<HTMLCanvasElement>>('wave');
  private audioCtx?: AudioContext;
  private rafId = 0;
  private levels: number[] = [];
  private escListener = (e: KeyboardEvent) => { if (e.key === 'Escape' && this.listening()) this.cancelDictation(); };

  async ngOnInit(): Promise<void> {
    const position = this.pointer.coarse()
      ? this.overlay.position().global().centerHorizontally().bottom('24px') // above home indicator, thumb-reachable
      : this.overlay.position().global().right('24px').bottom('24px');
    this.overlayRef = this.overlay.create({ positionStrategy: position, hasBackdrop: false });
    this.overlayRef.attach(new TemplatePortal(this.panelTpl(), this.vcr));
    try {
      const info = await this.data.sttEngine();
      this.available.set(info.available);
      const remembered = localStorage.getItem('synform-stt-engine') as SttEngine | null;
      this.engine.set(remembered && info.available.includes(remembered) ? remembered : info.engine);
    } catch {
      this.engine.set('none');
    }
  }

  selectEngine(event: Event): void {
    const value = (event.target as HTMLSelectElement).value as SttEngine;
    this.engine.set(value);
    localStorage.setItem('synform-stt-engine', value);
  }

  engineLabel(e: SttEngine): string {
    return e === 'whisper' ? 'Whisper (Azure)' : e === 'deepgram' ? 'Deepgram' : e === 'vibevoice' ? 'VibeVoice' : e;
  }

  /** Record-then-transcribe for every engine; the selected engine rides along as a form field. */
  private createSource(): AudioSource {
    return new VibeVoiceAudioSource(blob => this.data.transcribe(blob, this.layoutKey(), this.engine()));
  }

  ngOnDestroy(): void {
    this.stopWaveform();
    this.sub?.unsubscribe();
    this.source?.stop();
    this.overlayRef?.dispose();
  }

  toggle(): void {
    this.listening() ? this.stop() : this.start();
  }

  private start(): void {
    this.error.set('');
    this.summary.set('');
    this.listening.set(true);
    this.source = this.createSource();
    document.addEventListener('keydown', this.escListener);
    this.sub = this.source.start().subscribe({
      next: e => {
        this.transcript.set(e.text);
        if (e.utteranceEnd) this.handleUtterance(e.text);
      },
      error: err => {
        this.error.set(err?.error?.detail ?? err?.message ?? 'Microphone or transcription unavailable.');
        this.stopWaveform();
        this.listening.set(false);
        this.processing.set(false);
      },
      complete: () => {
        this.stopWaveform();
        this.listening.set(false);
      },
    });
    this.startWaveform();
  }

  cancelDictation(): void {
    this.stopWaveform();
    this.source?.cancel?.();
    this.sub?.unsubscribe();
    this.listening.set(false);
    this.processing.set(false);
    this.transcript.set('');
  }

  // ---------- waveform: client-side input-level bars, independent of the STT engine ----------

  private startWaveform(): void {
    const tryAttach = (attempt = 0): void => {
      const stream = this.source?.mediaStream;
      if (!stream) {
        if (attempt < 40 && this.listening()) setTimeout(() => tryAttach(attempt + 1), 50);
        return;
      }
      this.audioCtx = new AudioContext();
      const analyser = this.audioCtx.createAnalyser();
      analyser.fftSize = 512;
      this.audioCtx.createMediaStreamSource(stream).connect(analyser);
      const data = new Uint8Array(analyser.fftSize);
      this.levels = [];
      let lastSample = 0;

      const draw = (now: number) => {
        if (!this.listening()) return;
        if (now - lastSample > 45) { // ~22 bars/second scroll
          lastSample = now;
          analyser.getByteTimeDomainData(data);
          let sum = 0;
          for (let i = 0; i < data.length; i++) { const v = (data[i] - 128) / 128; sum += v * v; }
          this.levels.push(Math.min(1, Math.sqrt(sum / data.length) * 4));
          if (this.levels.length > 64) this.levels.shift();
        }
        const canvas = this.waveCanvas()?.nativeElement;
        const ctx = canvas?.getContext('2d');
        if (canvas && ctx) {
          ctx.clearRect(0, 0, canvas.width, canvas.height);
          const mid = canvas.height / 2;
          for (let i = 0; i < 64; i++) {
            const level = this.levels[this.levels.length - 64 + i] ?? -1;
            const x = i * 4 + 1;
            if (level < 0.02) { // silence (or not yet recorded) → baseline dot
              ctx.fillStyle = level < 0 ? 'rgba(255,255,255,.18)' : 'rgba(255,255,255,.45)';
              ctx.fillRect(x, mid - 1, 2, 2);
            } else {
              const h = Math.max(3, level * (canvas.height - 6));
              ctx.fillStyle = 'rgba(255,255,255,.9)';
              ctx.fillRect(x, mid - h / 2, 2, h);
            }
          }
        }
        this.rafId = requestAnimationFrame(draw);
      };
      this.rafId = requestAnimationFrame(draw);
    };
    tryAttach();
  }

  private stopWaveform(): void {
    cancelAnimationFrame(this.rafId);
    document.removeEventListener('keydown', this.escListener);
    void this.audioCtx?.close().catch(() => {});
    this.audioCtx = undefined;
  }

  private stop(): void {
    // Push-to-talk: stopping ends the recording; keep the subscription alive —
    // the transcript event arrives AFTER stop, once the backend transcribes the blob.
    this.processing.set(true);
    this.source?.stop();
  }

  private async handleUtterance(text: string): Promise<void> {
    this.log.update(entries => [...entries, text]);
    await this.extractAndPopulate(text);
  }

  private async extractAndPopulate(text: string): Promise<void> {
    this.processing.set(true);
    try {
      const extraction = await this.data.extract({
        layoutKey: this.layoutKey(),
        layoutVersion: this.layoutVersion(),
        inputType: 'transcript',
        text,
      });
      const result = this.synForm().populate(extraction.values, 'voice', extraction.confidences);
      const review = Object.values(extraction.confidences).filter(c => c < 0.85).length;
      this.summary.set(`${result.applied.length} field${result.applied.length === 1 ? '' : 's'} updated`
        + (review ? `, ${review} needs review` : '')
        + (result.skipped.length ? `, ${result.skipped.length} skipped` : '')
        + ` · ${this.engineLabel(this.engine())}`);
    } catch {
      this.error.set('Extraction failed — transcript kept. Tap mic to retry.');
    } finally {
      this.processing.set(false);
      this.transcript.set('');
    }
  }
}
