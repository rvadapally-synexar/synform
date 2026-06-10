import { Observable } from 'rxjs';

export interface TranscriptEvent {
  text: string;
  /** Deepgram endpointing: true when the utterance is complete (speech_final). */
  utteranceEnd: boolean;
}

/**
 * Audio path seam (spec §8). The POC ships WebAudioSource (getUserMedia → Deepgram WS).
 * A native Capacitor implementation (AVAudioEngine, 16kHz PCM) slots in behind the same
 * interface later — the seam is what the POC proves.
 */
export interface AudioSource {
  start(): Observable<TranscriptEvent>;
  stop(): void;
}

/** Browser implementation: MediaRecorder (webm/opus) streamed to Deepgram over WebSocket. */
export class WebAudioSource implements AudioSource {
  private ws?: WebSocket;
  private recorder?: MediaRecorder;
  private stream?: MediaStream;

  constructor(private getToken: () => Promise<string>) {}

  start(): Observable<TranscriptEvent> {
    return new Observable<TranscriptEvent>(subscriber => {
      (async () => {
        try {
          const token = await this.getToken();
          this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });

          const params = new URLSearchParams({
            model: 'nova-2', interim_results: 'true', endpointing: '300',
            smart_format: 'true', punctuate: 'true',
          });
          this.ws = new WebSocket(`wss://api.deepgram.com/v1/listen?${params}`, ['bearer', token]);

          this.ws.onopen = () => {
            this.recorder = new MediaRecorder(this.stream!, { mimeType: 'audio/webm;codecs=opus' });
            this.recorder.ondataavailable = e => {
              if (e.data.size > 0 && this.ws?.readyState === WebSocket.OPEN) this.ws.send(e.data);
            };
            this.recorder.start(250);
          };
          this.ws.onmessage = event => {
            const msg = JSON.parse(event.data);
            const text = msg.channel?.alternatives?.[0]?.transcript ?? '';
            if (msg.type === 'Results' && text)
              subscriber.next({ text, utteranceEnd: msg.is_final && msg.speech_final });
          };
          this.ws.onerror = () => subscriber.error(new Error('Deepgram connection failed.'));
          this.ws.onclose = () => subscriber.complete();
        } catch (e) {
          subscriber.error(e);
        }
      })();
      return () => this.stop();
    });
  }

  stop(): void {
    this.recorder?.state !== 'inactive' && this.recorder?.stop();
    this.stream?.getTracks().forEach(t => t.stop());
    this.ws?.close();
    this.recorder = undefined; this.stream = undefined; this.ws = undefined;
  }
}

/**
 * Native Capacitor implementation — interface only, NOT implemented in the POC (spec §8).
 * Would use a Capacitor plugin bridging AVAudioEngine (16kHz PCM mono) and stream the
 * buffers over the same Deepgram WebSocket with encoding=linear16&sample_rate=16000.
 * Requires NSMicrophoneUsageDescription in Info.plist.
 */
export class NativeAudioSource implements AudioSource {
  start(): Observable<TranscriptEvent> {
    throw new Error('NativeAudioSource is a post-POC implementation. Use WebAudioSource.');
  }
  stop(): void {}
}
