import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, finalize, throwError } from 'rxjs';
import { ErroApi } from './modelos';

/**
 * Contador de requisicoes em andamento. A barra de progresso do topo escuta este sinal.
 * E um contador (nao um booleano) porque varias chamadas podem estar no ar ao mesmo tempo:
 * com booleano, a primeira a terminar apagaria a barra das outras.
 */
@Injectable({ providedIn: 'root' })
export class EstadoDeCarregamento {
  private readonly emAndamento = signal(0);
  readonly carregando = this.emAndamento.asReadonly();

  comecou(): void {
    this.emAndamento.update((n) => n + 1);
  }

  terminou(): void {
    this.emAndamento.update((n) => Math.max(0, n - 1));
  }
}

export const carregandoInterceptor: HttpInterceptorFn = (req, next) => {
  // A vigilancia de saude bate de 5 em 5 segundos. Se ela contasse aqui,
  // a barra de progresso piscaria para sempre e perderia todo o significado.
  if (req.url.includes('/health')) return next(req);

  const estado = inject(EstadoDeCarregamento);
  estado.comecou();
  return next(req).pipe(finalize(() => estado.terminou()));
};

/**
 * Traduz o erro HTTP cru para o formato que a tela entende.
 *
 * As duas APIs devolvem ProblemDetails (RFC 7807) com um campo "codigo" estavel.
 * Sem esta traducao, cada componente teria que saber ler HttpErrorResponse,
 * adivinhar onde esta a mensagem e tratar o caso de "nem chegou no servidor".
 */
export const erroInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((erro: HttpErrorResponse) => throwError(() => traduzir(erro))),
  );

function traduzir(erro: HttpErrorResponse): ErroApi {
  // status 0 = o navegador nem conseguiu falar com o servidor
  // (servico desligado, CORS bloqueado, rede caiu).
  if (erro.status === 0) {
    return {
      status: 0,
      codigo: 'SEM_CONEXAO',
      mensagem:
        'Nao foi possivel falar com o servidor. Verifique se os servicos estao rodando.',
      ehIndisponibilidade: true,
    };
  }

  const corpo = erro.error ?? {};
  const codigo: string = corpo.codigo ?? 'ERRO';
  const mensagem: string =
    corpo.title ?? corpo.mensagem ?? erro.message ?? 'Erro inesperado.';

  return {
    status: erro.status,
    codigo,
    mensagem,
    detalhes: corpo.detalhes,
    ehIndisponibilidade:
      erro.status === 503 ||
      codigo === 'ESTOQUE_INDISPONIVEL' ||
      codigo === 'SERVICO_INDISPONIVEL',
  };
}
