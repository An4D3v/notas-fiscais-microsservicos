import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { API_CONFIG } from '../api.config';
import {
  Interpretacao,
  ItemNotaEnvio,
  Nota,
  Pagina,
  ResultadoImpressao,
  StatusNota,
} from '../modelos';

@Injectable({ providedIn: 'root' })
export class NotasService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_CONFIG).faturamento;

  /**
   * Chave de idempotencia por nota.
   *
   * A regra: enquanto a MESMA impressao nao terminar, toda tentativa usa a MESMA chave.
   * Se a pessoa clicar de novo depois de um erro de rede, o servidor reconhece o pedido
   * e devolve o resultado anterior em vez de baixar o estoque outra vez.
   * A chave so e descartada quando a impressao chega a um desfecho definitivo.
   */
  private readonly chavesDeImpressao = new Map<number, string>();

  listar(status?: StatusNota, pagina = 1, tamanho = 20): Observable<Pagina<Nota>> {
    let params = new HttpParams().set('pagina', pagina).set('tamanho', tamanho);
    if (status) params = params.set('status', status);

    return this.http.get<Pagina<Nota>>(`${this.base}/notas`, { params });
  }

  buscar(id: number): Observable<Nota> {
    return this.http.get<Nota>(`${this.base}/notas/${id}`);
  }

  criar(itens: ItemNotaEnvio[]): Observable<Nota> {
    return this.http.post<Nota>(`${this.base}/notas`, { itens });
  }

  /**
   * Transforma texto livre numa SUGESTAO de itens. Nao cria nada:
   * quem confirma e a pessoa, na tela.
   */
  interpretar(texto: string): Observable<Interpretacao> {
    return this.http.post<Interpretacao>(`${this.base}/notas/interpretar`, { texto });
  }

  adicionarItens(id: number, itens: ItemNotaEnvio[]): Observable<Nota> {
    return this.http.post<Nota>(`${this.base}/notas/${id}/itens`, { itens });
  }

  removerItem(id: number, itemId: number): Observable<Nota> {
    return this.http.delete<Nota>(`${this.base}/notas/${id}/itens/${itemId}`);
  }

  excluir(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/notas/${id}`);
  }

  imprimir(id: number): Observable<ResultadoImpressao> {
    const chave = this.chaveDe(id);
    const headers = new HttpHeaders({ 'Idempotency-Key': chave });

    return this.http
      .post<ResultadoImpressao>(`${this.base}/notas/${id}/imprimir`, {}, { headers })
      // Deu certo: a impressao acabou, a chave nao serve mais para nada.
      .pipe(tap(() => this.chavesDeImpressao.delete(id)));
  }

  /**
   * Chamado quando o erro e DEFINITIVO (saldo insuficiente, nota ja impressa).
   * Nesses casos a proxima tentativa e um pedido novo, e merece chave nova.
   * Em erro de indisponibilidade a chave e mantida de proposito.
   */
  descartarChave(id: number): void {
    this.chavesDeImpressao.delete(id);
  }

  private chaveDe(id: number): string {
    const existente = this.chavesDeImpressao.get(id);
    if (existente) return existente;

    const nova = `nota-${id}-${crypto.randomUUID()}`;
    this.chavesDeImpressao.set(id, nova);
    return nova;
  }
}
