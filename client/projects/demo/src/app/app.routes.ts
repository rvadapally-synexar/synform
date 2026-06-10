import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./pages/demo-form.page').then(m => m.DemoFormPage) },
  { path: 'maintainer', loadComponent: () => import('./pages/layout-list.page').then(m => m.LayoutListPage) },
  { path: 'maintainer/lookups', loadComponent: () => import('./pages/lookups.page').then(m => m.LookupsPage) },
  { path: 'maintainer/:layoutKey', loadComponent: () => import('./pages/maintainer.page').then(m => m.MaintainerPage) },
  { path: 'records', loadComponent: () => import('./pages/records.page').then(m => m.RecordsPage) },
  { path: '**', redirectTo: '' },
];
