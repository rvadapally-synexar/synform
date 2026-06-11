import { CommonModule } from '@angular/common';
import { Overlay, OverlayModule, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import {
  ChangeDetectionStrategy, Component, OnDestroy, OnInit, TemplateRef,
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
        <button type="button" class="mic" (click)="toggle()"
                [attr.aria-label]="listening() ? 'Stop dictation' : 'Start dictation'">
          {{ listening() ? '◼' : '🎤' }}
        </button>
        <div class="status">
          @if (error()) { <span class="err">{{ error() }}</span> }
          @else if (processing()) { <span class="spin" aria-hidden="true"></span> Transcribing… }
          @else if (transcript()) { <span class="transcript">{{ transcript() }}</span> }
          @else if (listening()) { {{ engine() === 'deepgram' ? 'Listening…' : 'Recording — tap ◼ when done' }} }
          @else if (summary()) { <span class="summary">{{ summary() }}</span> }
          @else { Tap the mic and dictate }
        </div>
      </div>
    </ng-template>
  `,
  styles: [`
    .voice-panel {
      display: flex; align-items: center; gap: 12px;
      background: #1f2937; color: #f9fafb; border-radius: 28px;
      padding: 8px 20px 8px 8px; box-shadow: 0 8px 24px rgb(0 0 0 / .35);
      max-width: min(520px, 92vw); font-size: 14px;
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
    this.sub = this.source.start().subscribe({
      next: e => {
        this.transcript.set(e.text);
        if (e.utteranceEnd) this.handleUtterance(e.text);
      },
      error: err => {
        this.error.set(err?.error?.detail ?? err?.message ?? 'Microphone or transcription unavailable.');
        this.listening.set(false);
        this.processing.set(false);
      },
      complete: () => this.listening.set(false),
    });
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
