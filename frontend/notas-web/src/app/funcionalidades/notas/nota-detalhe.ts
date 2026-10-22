import { DatePipe } from '@angular/common';
import { Component, Input, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Subject, takeUntil } from 'rxjs';
import { SeloStatus } from '../../compartilhado/selo-status';
import { ErroApi, Nota, SaldoAtualizado } from '../../nucleo/modelos';
import { NotasService } from '../../nucleo/servicos/notas.service';
import { NotificacaoService } from '../../nucleo/servicos/notificacao.service';

@Component({
  selector: 'nf-nota-detalhe',
  imports: [
    DatePipe,
    RouterLink,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    SeloStatus,
  ],
  templateUrl: './nota-detalhe.html',
  styleUrl: './nota-detalhe.scss',
})
export class NotaDetalhe implements OnInit, OnDestroy {
  private readonly servico = inject(NotasService);
  private readonly aviso = inject(NotificacaoService);

  /** Preenchido pelo router (withComponentInputBinding) a partir da rota /notas/:id. */
  @Input() id!: string;

  protected readonly colunas = ['codigo', 'descricao', 'quantidade'];
  protected readonly nota = signal<Nota | null>(null);
  protected readonly carregando = signal(true);

  /** true enquanto a impressao esta em andamento. E o que segura o indicador de processamento. */
  protected readonly imprimindo = signal(false);

  /** Saldos devolvidos pela impressao, mostrados como comprovante do que foi baixado. */
  protected readonly saldos = signal<SaldoAtualizado[]>([]);
  protected readonly foiRepetida = signal(false);

  private readonly destruir$ = new Subject<void>();

  ngOnInit(): void {
    this.carregar();
  }

  ngOnDestroy(): void {
    this.destruir$.next();
    this.destruir$.complete();
  }

  private carregar(): void {
    this.carregando.set(true);
    this.servico
      .buscar(Number(this.id))
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: (nota) => {
          this.nota.set(nota);
          this.carregando.set(false);
        },
        error: (erro: ErroApi) => {
          this.carregando.set(false);
          this.aviso.erro(erro);
        },
      });
  }

  /**
   * O botao Imprimir.
   *
   * O indicador de processamento fica visivel durante TODA a operacao, que por baixo
   * envolve a chamada ao servico de Estoque com tentativas automaticas. Por isso ela
   * pode levar alguns segundos quando o Estoque esta com problema: o giro do indicador
   * corresponde a trabalho real, e nao a um atraso artificial.
   */
  protected imprimir(): void {
    const atual = this.nota();
    if (!atual || this.imprimindo()) return;

    this.imprimindo.set(true);
    this.saldos.set([]);

    this.servico
      .imprimir(atual.id)
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: (resultado) => {
          this.imprimindo.set(false);
          this.nota.set(resultado.nota);
          this.saldos.set(resultado.saldosAtualizados);
          this.foiRepetida.set(resultado.repetido);

          if (resultado.repetido) {
            this.aviso.aviso(
              `A nota ${resultado.nota.numero} ja tinha sido impressa neste pedido. ` +
                'Nenhum saldo foi baixado de novo.',
            );
          } else {
            this.aviso.sucesso(`Nota ${resultado.nota.numero} impressa e fechada.`);
          }
        },
        error: (erro: ErroApi) => {
          this.imprimindo.set(false);
          this.aviso.erro(erro);

          // Erro definitivo (saldo insuficiente, nota ja impressa) encerra este pedido:
          // a proxima tentativa e um pedido NOVO e merece uma chave nova.
          // Em indisponibilidade a chave e mantida, para o retry continuar sendo seguro.
          if (!erro.ehIndisponibilidade) this.servico.descartarChave(atual.id);

          // Recarrega para mostrar o status real (pode ter voltado para Aberta
          // ou ficado em Processando) e o motivo gravado pelo servidor.
          this.carregar();
        },
      });
  }

  protected totalUnidades(): number {
    return this.nota()?.itens.reduce((soma, i) => soma + i.quantidade, 0) ?? 0;
  }

  protected podeImprimir(): boolean {
    const n = this.nota();
    return !!n && n.status !== 'Fechada' && n.itens.length > 0;
  }
}
