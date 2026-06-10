/*
 * Public API surface of the syn-form library.
 */
export * from './lib/types';
export * from './lib/condition';
export * from './lib/computed';
export { SYN_FORM_API_BASE, SynFormDataService } from './lib/data.service';
export { PointerModeService } from './lib/pointer';
export { SynFormComponent } from './lib/syn-form.component';
export { SynFieldComponent } from './lib/field.component';
export { SynFormGroupDirective } from './lib/group.directive';
export { SynVoicePanelComponent } from './lib/voice-panel.component';
export { type AudioSource, type TranscriptEvent, WebAudioSource, NativeAudioSource } from './lib/audio-source';
