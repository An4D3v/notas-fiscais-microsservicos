import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'produtos', pathMatch: 'full' },

  // Carregamento sob demanda (lazy loading): cada tela vira um pedaco separado
  // do bundle e so e baixada quando a pessoa entra nela.
  {
    path: 'produtos',
    title: 'Produtos | Notas Fiscais',
    loadComponent: () =>
      import('./funcionalidades/produtos/produtos-pagina').then((m) => m.ProdutosPagina),
  },
  {
    path: 'notas',
    title: 'Notas fiscais | Notas Fiscais',
    loadComponent: () =>
      import('./funcionalidades/notas/notas-pagina').then((m) => m.NotasPagina),
  },
  {
    path: 'notas/nova',
    title: 'Nova nota | Notas Fiscais',
    loadComponent: () =>
      import('./funcionalidades/notas/nota-nova').then((m) => m.NotaNova),
  },
  {
    path: 'notas/:id',
    title: 'Nota fiscal | Notas Fiscais',
    loadComponent: () =>
      import('./funcionalidades/notas/nota-detalhe').then((m) => m.NotaDetalhe),
  },

  { path: '**', redirectTo: 'produtos' },
];
