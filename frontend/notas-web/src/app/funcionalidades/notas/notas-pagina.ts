import { DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import {
  BehaviorSubject,
  Subject,
  catchError,
  of,
  startWith,
  switchMap,
  takeUntil,
} from 'rxjs';
import { SeloStatus } from '../../compartilhado/selo-status';
import { ErroApi, Nota, StatusNota } from '../../nucleo/modelos';
import { NotasService } from '../../nucleo/servicos/notas.service';
import { NotificacaoService } from '../../nucleo/servicos/notificacao.service';

@Component({
  selector: 'nf-notas-pagina',
  imports: [
    DatePipe,
    RouterLink,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    SeloStatus,
  ],
  templateUrl: './notas-pagina.html',
  styleUrl: './notas-pagina.scss',
})
export class NotasPagina implements OnInit, OnDestroy {
  private readonly servico = inject(NotasService);
  private readonly aviso = inject(NotificacaoService);
  private readonly router = inject(Router);

  protected readonly colunas = ['numero', 'status', 'itens', 'criadaEm', 'impressaEm', 'acoes'];

  /** Opcoes do filtro de status, na ordem em que a nota caminha. */
  protected readonly filtros: { valor: StatusNota | ''; rotulo: string }[] = [
    { valor: '', rotulo: 'Todas' },
    { valor: 'Aberta', rotulo: 'Abertas' },
    { valor: 'Processando', rotulo: 'Processando' },
    { valor: 'Fechada', rotulo: 'Fechadas' },
  ];
  protected readonly notas = signal<Nota[]>([]);
  protected readonly filtro = signal<StatusNota | ''>('');

  private readonly filtro$ = new BehaviorSubject<StatusNota | ''>('');
  private readonly recarregar$ = new Subject<void>();
  private readonly destruir$ = new Subject<void>();

  ngOnInit(): void {
    this.filtro$
      .pipe(
        switchMap((status) =>
          this.recarregar$.pipe(
            startWith(undefined),
            switchMap(() =>
              this.servico.listar(status || undefined, 1, 100).pipe(
                catchError((erro: ErroApi) => {
                  this.aviso.erro(erro);
                  return of({ itens: [], total: 0, pagina: 1, tamanho: 100 });
                }),
              ),
            ),
          ),
        ),
        takeUntil(this.destruir$),
      )
      .subscribe((pagina) => this.notas.set(pagina.itens));
  }

  ngOnDestroy(): void {
    this.destruir$.next();
    this.destruir$.complete();
  }

  protected filtrar(status: StatusNota | ''): void {
    this.filtro.set(status);
    this.filtro$.next(status);
  }

  protected abrir(nota: Nota): void {
    this.router.navigate(['/notas', nota.id]);
  }

  protected excluir(nota: Nota, evento: MouseEvent): void {
    evento.stopPropagation();
    if (!confirm(`Excluir a nota numero ${nota.numero}?`)) return;

    this.servico
      .excluir(nota.id)
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: () => {
          this.aviso.sucesso(`Nota ${nota.numero} excluida.`);
          this.recarregar$.next();
        },
        error: (erro: ErroApi) => this.aviso.erro(erro),
      });
  }

  protected totalItens(nota: Nota): number {
    return nota.itens.reduce((soma, item) => soma + item.quantidade, 0);
  }
}
