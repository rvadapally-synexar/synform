import { Injectable, signal } from '@angular/core';

/**
 * Surface-variant decision (spec §4.1): defaults to the (pointer: coarse) media query,
 * but is overridable so the Maintainer's "Preview on touch" toggle can force coarse
 * rendering from a desktop. No isNative/platform conditionals anywhere in component logic.
 */
@Injectable({ providedIn: 'root' })
export class PointerModeService {
  private override = signal<boolean | null>(null);
  private mediaCoarse = signal(false);

  readonly coarse = () => this.override() ?? this.mediaCoarse();

  constructor() {
    if (typeof window !== 'undefined' && window.matchMedia) {
      const mq = window.matchMedia('(pointer: coarse)');
      this.mediaCoarse.set(mq.matches);
      mq.addEventListener('change', e => this.mediaCoarse.set(e.matches));
    }
  }

  /** Force coarse (true), fine (false), or media-query default (null). */
  setOverride(value: boolean | null): void {
    this.override.set(value);
  }
}
