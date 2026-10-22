import { registerLocaleData } from '@angular/common';
import localePt from '@angular/common/locales/pt';
import { LOCALE_ID, Provider } from '@angular/core';
import { MatPaginatorIntl } from '@angular/material/paginator';

/**
 * O Angular Material vem em ingles de fabrica. Sem isto, um sistema inteiro
 * em portugues exibe "Items per page" e "1 - 6 of 6" no rodape da tabela,
 * que e o tipo de detalhe que faz a tela parecer inacabada.
 */
export function paginadorEmPortugues(): MatPaginatorIntl {
  const intl = new MatPaginatorIntl();

  intl.itemsPerPageLabel = 'Itens por página';
  intl.nextPageLabel = 'Próxima página';
  intl.previousPageLabel = 'Página anterior';
  intl.firstPageLabel = 'Primeira página';
  intl.lastPageLabel = 'Última página';

  intl.getRangeLabel = (pagina: number, tamanho: number, total: number): string => {
    if (total === 0) return '0 de 0';

    const inicio = pagina * tamanho + 1;
    const fim = Math.min(inicio + tamanho - 1, total);
    return `${inicio}–${fim} de ${total}`;
  };

  return intl;
}

registerLocaleData(localePt);

/** Data, hora e numero no formato daqui. */
export const provedoresDeIdioma: Provider[] = [
  { provide: LOCALE_ID, useValue: 'pt-BR' },
  { provide: MatPaginatorIntl, useFactory: paginadorEmPortugues },
];
