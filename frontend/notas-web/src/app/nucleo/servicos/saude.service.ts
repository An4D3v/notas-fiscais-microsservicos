import { HttpClient } from '@angular/common/http';
import { DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap, timer } from 'rxjs';
import { API_CONFIG } from '../api.config';

/**
 * Vigia dos dois servicos.
 *
 * Bate em /health de cada um a cada 5 segundos e publica o resultado em sinais.
 * A barra do topo mostra isso ao vivo, entao quando o Estoque cai a tela AVISA,
 * em vez de a pessoa descobrir so depois de clicar em Imprimir e tomar erro.
 *
 * RxJS usado aqui:
 *   timer(0, 5000)  -> dispara agora e depois de 5 em 5 segundos
 *   switchMap       -> troca a chamada anterior pela nova (se a antiga travou, e descartada)
 *   catchError      -> falha vira "fora do ar", e o fluxo NAO morre
 *   takeUntilDestroyed -> encerra tudo quando o servico e destruido (nao vaza memoria)
 */
@Injectable({ providedIn: 'root' })
export class SaudeService {
  private readonly http = inject(HttpClient);
  private readonly api = inject(API_CONFIG);
  private readonly destroyRef = inject(DestroyRef);

  private readonly _estoque = signal<boolean | null>(null);
  private readonly _faturamento = signal<boolean | null>(null);

  /** null enquanto ainda nao sabemos, true no ar, false fora do ar. */
  readonly estoqueNoAr = this._estoque.asReadonly();
  readonly faturamentoNoAr = this._faturamento.asReadonly();

  constructor() {
    this.vigiar(this.api.estoque).subscribe((ok) => this._estoque.set(ok));
    this.vigiar(this.api.faturamento).subscribe((ok) => this._faturamento.set(ok));
  }

  private vigiar(base: string) {
    return timer(0, 5000).pipe(
      switchMap(() =>
        this.http.get(`${base}/health`, { responseType: 'text' }).pipe(
          map(() => true),
          catchError(() => of(false)),
        ),
      ),
      takeUntilDestroyed(this.destroyRef),
    );
  }
}
