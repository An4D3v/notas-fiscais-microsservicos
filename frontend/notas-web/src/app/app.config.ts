import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provedoresDeIdioma } from './nucleo/idioma';
import { carregandoInterceptor, erroInterceptor } from './nucleo/interceptores';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),

    // withComponentInputBinding: o parametro da rota (:id) chega no componente
    // como @Input, sem precisar assinar o ActivatedRoute na mao.
    provideRouter(routes, withComponentInputBinding()),

    // Sem provideAnimations: a partir do Angular Material 21 os componentes usam
    // animacao em CSS puro, entao o pacote @angular/animations deixou de ser necessario.

    // A ordem importa: carregando envolve tudo (inclusive o erro), e o erro
    // e o mais interno, para traduzir a falha antes de qualquer outro tratamento.
    provideHttpClient(withInterceptors([carregandoInterceptor, erroInterceptor])),

    // Data, numero e os textos do paginador em portugues do Brasil.
    ...provedoresDeIdioma,
  ],
};
