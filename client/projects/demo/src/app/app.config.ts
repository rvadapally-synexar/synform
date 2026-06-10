import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { SYN_FORM_API_BASE } from 'syn-form';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(),
    // Same-host API: works on localhost and when served over the LAN (iPad hitting http://<pc-ip>:4200).
    { provide: SYN_FORM_API_BASE, useValue: `http://${location.hostname}:5266` },
  ],
};
