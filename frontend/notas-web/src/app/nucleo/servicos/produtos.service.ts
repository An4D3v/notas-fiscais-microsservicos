import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { API_CONFIG } from '../api.config';
import { AtualizarProduto, CriarProduto, Pagina, Produto } from '../modelos';

@Injectable({ providedIn: 'root' })
export class ProdutosService {
  private readonly http = inject(HttpClient);
  private readonly base = inject(API_CONFIG).estoque;

  listar(busca = '', pagina = 1, tamanho = 20): Observable<Pagina<Produto>> {
    let params = new HttpParams().set('pagina', pagina).set('tamanho', tamanho);
    if (busca.trim()) params = params.set('busca', busca.trim());

    return this.http.get<Pagina<Produto>>(`${this.base}/produtos`, { params });
  }

  buscar(id: number): Observable<Produto> {
    return this.http.get<Produto>(`${this.base}/produtos/${id}`);
  }

  criar(produto: CriarProduto): Observable<Produto> {
    return this.http.post<Produto>(`${this.base}/produtos`, produto);
  }

  atualizar(id: number, produto: AtualizarProduto): Observable<Produto> {
    return this.http.put<Produto>(`${this.base}/produtos/${id}`, produto);
  }

  excluir(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/produtos/${id}`);
  }
}
