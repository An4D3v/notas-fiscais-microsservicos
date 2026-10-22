import {
  AfterViewInit,
  Component,
  OnDestroy,
  OnInit,
  ViewChild,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginator, MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  BehaviorSubject,
  Subject,
  catchError,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  filter,
  of,
  startWith,
  switchMap,
  takeUntil,
} from 'rxjs';
import { ErroApi, Pagina, Produto } from '../../nucleo/modelos';
import { NotificacaoService } from '../../nucleo/servicos/notificacao.service';
import { ProdutosService } from '../../nucleo/servicos/produtos.service';
import { ProdutoFormulario } from './produto-formulario';

const PAGINA_VAZIA: Pagina<Produto> = { itens: [], total: 0, pagina: 1, tamanho: 10 };

@Component({
  selector: 'nf-produtos-pagina',
  imports: [
    FormsModule,
    MatTableModule,
    MatPaginatorModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatDialogModule,
    MatTooltipModule,
  ],
  templateUrl: './produtos-pagina.html',
  styleUrl: './produtos-pagina.scss',
})
export class ProdutosPagina implements OnInit, AfterViewInit, OnDestroy {
  private readonly servico = inject(ProdutosService);
  private readonly aviso = inject(NotificacaoService);
  private readonly dialogo = inject(MatDialog);

  // ViewChild resolvido depois que a view existe: por isso a assinatura do
  // paginador acontece em ngAfterViewInit, e nao em ngOnInit.
  @ViewChild(MatPaginator) private paginador?: MatPaginator;

  protected readonly colunas = ['codigo', 'descricao', 'saldo', 'acoes'];
  protected readonly produtos = signal<Produto[]>([]);
  protected readonly total = signal(0);
  protected readonly tamanhoPagina = signal(10);
  protected readonly termoBusca = signal('');

  // --- Fontes do fluxo -------------------------------------------------
  private readonly busca$ = new BehaviorSubject<string>('');
  private readonly paginacao$ = new BehaviorSubject<PageEvent | null>(null);
  private readonly recarregar$ = new Subject<void>();

  /**
   * Sinal de fim de vida do componente.
   * Toda inscricao termina com takeUntil(destruir$), e ngOnDestroy dispara o sinal.
   * Sem isso, sair da tela no meio de uma busca deixaria a inscricao viva,
   * escrevendo em um componente que ja nao existe (vazamento de memoria).
   */
  private readonly destruir$ = new Subject<void>();

  // ---------------------------------------------------------------------
  ngOnInit(): void {
    combineLatest([
      // debounceTime: espera a pessoa parar de digitar antes de ir ao servidor.
      // distinctUntilChanged: se o texto final for igual ao anterior, nao repete a chamada.
      this.busca$.pipe(debounceTime(350), distinctUntilChanged()),
      this.paginacao$,
      this.recarregar$.pipe(startWith(undefined)),
    ])
      .pipe(
        // switchMap: se uma busca nova chega antes da anterior responder, a anterior
        // e CANCELADA. Isso evita a resposta antiga chegar depois e sobrescrever a nova.
        switchMap(([busca, pagina]) =>
          this.servico
            .listar(busca, (pagina?.pageIndex ?? 0) + 1, pagina?.pageSize ?? 10)
            .pipe(
              // catchError DENTRO do switchMap: o erro derruba so esta chamada.
              // Se estivesse fora, o primeiro erro mataria o fluxo e a tela
              // pararia de responder a qualquer busca seguinte.
              catchError((erro: ErroApi) => {
                this.aviso.erro(erro);
                return of(PAGINA_VAZIA);
              }),
            ),
        ),
        takeUntil(this.destruir$),
      )
      .subscribe((pagina) => {
        this.produtos.set(pagina.itens);
        this.total.set(pagina.total);
      });
  }

  ngAfterViewInit(): void {
    this.paginador?.page
      .pipe(takeUntil(this.destruir$))
      .subscribe((evento) => this.paginacao$.next(evento));
  }

  ngOnDestroy(): void {
    this.destruir$.next();
    this.destruir$.complete();
  }

  // ---------------------------------------------------------------------
  protected aoDigitar(texto: string): void {
    this.termoBusca.set(texto);
    if (this.paginador) this.paginador.pageIndex = 0;
    this.paginacao$.next({
      pageIndex: 0,
      pageSize: this.paginador?.pageSize ?? 10,
      length: this.total(),
    });
    this.busca$.next(texto);
  }

  protected limparBusca(): void {
    this.aoDigitar('');
  }

  protected novo(): void {
    this.dialogo
      .open(ProdutoFormulario, { data: {} })
      .afterClosed()
      .pipe(
        filter(Boolean),
        switchMap((valor) => this.servico.criar(valor)),
        takeUntil(this.destruir$),
      )
      .subscribe({
        next: (produto) => {
          this.aviso.sucesso(`Produto ${produto.codigo} cadastrado.`);
          this.recarregar$.next();
        },
        error: (erro: ErroApi) => this.aviso.erro(erro),
      });
  }

  protected editar(produto: Produto): void {
    this.dialogo
      .open(ProdutoFormulario, { data: { produto } })
      .afterClosed()
      .pipe(
        filter(Boolean),
        switchMap((valor) =>
          this.servico.atualizar(produto.id, {
            descricao: valor.descricao,
            saldo: valor.saldo,
          }),
        ),
        takeUntil(this.destruir$),
      )
      .subscribe({
        next: () => {
          this.aviso.sucesso(`Produto ${produto.codigo} atualizado.`);
          this.recarregar$.next();
        },
        error: (erro: ErroApi) => this.aviso.erro(erro),
      });
  }

  protected excluir(produto: Produto): void {
    if (!confirm(`Excluir o produto ${produto.codigo}?`)) return;

    this.servico
      .excluir(produto.id)
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: () => {
          this.aviso.sucesso(`Produto ${produto.codigo} excluido.`);
          this.recarregar$.next();
        },
        error: (erro: ErroApi) => this.aviso.erro(erro),
      });
  }

  /** Saldo normal nao recebe classe: fica preto, como qualquer outro numero. */
  protected classeSaldo(saldo: number): string {
    if (saldo === 0) return 'saldo-zero';
    if (saldo <= 3) return 'saldo-baixo';
    return '';
  }
}
