import { InjectionToken } from '@angular/core';
import { environment } from '../../environments/environment';

export interface ApiConfig {
  estoque: string;
  faturamento: string;
}

/**
 * Endereco dos dois servicos, injetado em vez de escrito no meio do codigo.
 * Assim trocar de ambiente (ou apontar para outra maquina) e uma linha de configuracao,
 * e os testes conseguem injetar um endereco falso.
 */
export const API_CONFIG = new InjectionToken<ApiConfig>('API_CONFIG', {
  providedIn: 'root',
  factory: () => ({
    estoque: environment.estoqueUrl,
    faturamento: environment.faturamentoUrl,
  }),
});
