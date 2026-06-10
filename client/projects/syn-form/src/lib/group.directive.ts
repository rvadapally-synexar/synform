import { Directive, ElementRef, inject, input } from '@angular/core';

/**
 * Marks a host-page container as the rendering slot for one layout group (spec §5).
 * Group→position mapping stays host-page markup (design-time); field metadata stays runtime.
 * The component renders each group's fields and ports them into the slot element
 * (DOM move — the views stay owned by syn-form's change detection).
 *
 *   <syn-form layoutKey="...">
 *     <div class="left" synFormGroup="patient"></div>
 *     <div class="right" synFormGroup="airway"></div>
 *   </syn-form>
 */
@Directive({ selector: '[synFormGroup]', standalone: true })
export class SynFormGroupDirective {
  group = input.required<string>({ alias: 'synFormGroup' });
  readonly element: HTMLElement = inject(ElementRef).nativeElement;
}
