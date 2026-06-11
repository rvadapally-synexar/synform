import { CommonModule } from '@angular/common';
import { Overlay, OverlayModule, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, TemplateRef,
  ViewContainerRef, inject, input, signal, viewChild,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { AudioSource, VibeVoiceAudioSource, WebAudioSource } from './audio-source';
import { SynFormDataService } from './data.service';
import { PointerModeService } from './pointer';
import { SynFormComponent } from './syn-form.component';

/**
 * <syn-voice-panel> — floating dictation panel (spec §8). CDK overlay: bottom-right on
 * desktop, bottom-center (thumb-reachable) on touch. On each completed utterance the
 * final transcript goes to /extract and the result flows through synForm.populate() —
 * the same seam as every other modality.
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
              @else if (processing()) { <span class="spin" aria-hidden="true"></span> Transcribing… }
              @else if (summary()) { <span class="summary">{{ summary() }}</span> }
              @else { Tap the mic and dictate }
            </div>
          } @else {
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
  engine = signal<'whisper' | 'vibevoice' | 'deepgram' | 'none'>('none');
  /** Every transcribed utterance, newest last — the user sees exactly what the ASR heard. */
  log = signal<string[]>([]);
  /** Batch engines record until tap-stop, then transcribe; deepgram streams live. */
  private isBatch = () => this.engine() === 'vibevoice' || this.engine() === 'whisper';

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
    try { this.engine.set((await this.data.sttEngine()).engine); } catch { this.engine.set('none'); }
  }

  /** Both engines share the AudioSource seam — only the lifecycle differs:
   * deepgram streams continuously; vibevoice records until stop, then emits once. */
  private createSource(): AudioSource {
    return this.isBatch()
      ? new VibeVoiceAudioSource(blob => this.data.transcribe(blob, this.layoutKey()))
      : new WebAudioSource(async () => (await this.data.sttToken()).access_token);
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
    if (this.isBatch()) {
      // Push-to-talk: stopping ends the recording; keep the subscription alive —
      // the transcript event arrives AFTER stop, once the backend transcribes the blob.
      this.processing.set(true);
      this.source?.stop();
      return;
    }
    this.sub?.unsubscribe();
    this.source?.stop();
    this.listening.set(false);
    this.transcript.set('');
  }

  private async handleUtterance(text: string): Promise<void> {
    this.processing.set(true);
    this.log.update(entries => [...entries, text]);
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
        + (result.skipped.length ? `, ${result.skipped.length} skipped` : ''));
    } catch {
      this.error.set('Extraction failed — transcript kept. Tap mic to retry.');
    } finally {
      this.processing.set(false);
      this.transcript.set('');
    }
  }
}
