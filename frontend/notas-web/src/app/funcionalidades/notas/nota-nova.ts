import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormControl, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import {
  Subject,
  catchError,
  debounceTime,
  distinctUntilChanged,
  of,
  startWith,
  switchMap,
  takeUntil,
} from 'rxjs';
import { ErroApi, Interpretacao, ItemNotaEnvio, Produto } from '../../nucleo/modelos';
import { NotasService } from '../../nucleo/servicos/notas.service';
import { NotificacaoService } from '../../nucleo/servicos/notificacao.service';
import { ProdutosService } from '../../nucleo/servicos/produtos.service';

@Component({
  selector: 'nf-nota-nova',
  imports: [
    DecimalPipe,
    FormsModule,
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatIconModule,
    MatTableModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './nota-nova.html',
  styleUrl: './nota-nova.scss',
})
export class NotaNova implements OnInit, OnDestroy {
  private readonly produtosApi = inject(ProdutosService);
  private readonly notasApi = inject(NotasService);
  private readonly aviso = inject(NotificacaoService);
  private readonly router = inject(Router);

  protected readonly colunas = ['codigo', 'descricao', 'quantidade', 'acoes'];

  /** Campo de busca do produto. Reactive Forms para poder escutar valueChanges. */
  protected readonly buscaProduto = new FormControl<string | Produto>('');

  protected readonly sugestoes = signal<Produto[]>([]);
  protected readonly selecionado = signal<Produto | null>(null);
  protected readonly quantidade = signal(1);
  protected readonly itens = signal<ItemNotaEnvio[]>([]);
  protected readonly salvando = signal(false);

  // --- montagem por texto ---
  protected readonly textoLivre = signal('');
  protected readonly interpretando = signal(false);
  protected readonly sugestao = signal<Interpretacao | null>(null);

  private readonly destruir$ = new Subject<void>();

  ngOnInit(): void {
    this.buscaProduto.valueChanges
      .pipe(
        startWith(''),
        // Sem debounce, cada tecla viraria uma chamada HTTP.
        debounceTime(300),
        distinctUntilChanged(),
        switchMap((valor) => {
          // Quando a pessoa escolhe uma opcao, o valor vira o objeto Produto.
          // Nesse caso nao ha o que buscar.
          if (valor && typeof valor === 'object') {
            this.selecionado.set(valor);
            return of({ itens: [valor], total: 1, pagina: 1, tamanho: 1 });
          }

          this.selecionado.set(null);
          return this.produtosApi.listar((valor ?? '') as string, 1, 8).pipe(
            catchError((erro: ErroApi) => {
              this.aviso.erro(erro);
              return of({ itens: [], total: 0, pagina: 1, tamanho: 8 });
            }),
          );
        }),
        takeUntil(this.destruir$),
      )
      .subscribe((pagina) => this.sugestoes.set(pagina.itens));
  }

  ngOnDestroy(): void {
    this.destruir$.next();
    this.destruir$.complete();
  }

  /** Como mostrar o produto escolhido dentro do campo de texto. */
  protected exibirProduto(valor: Produto | string | null): string {
    if (!valor) return '';
    return typeof valor === 'string' ? valor : `${valor.codigo} - ${valor.descricao}`;
  }

  protected adicionar(): void {
    const produto = this.selecionado();
    const qtd = Number(this.quantidade());

    if (!produto) {
      this.aviso.aviso('Escolha um produto na lista antes de adicionar.');
      return;
    }
    if (!Number.isInteger(qtd) || qtd <= 0) {
      this.aviso.aviso('A quantidade precisa ser um numero inteiro maior que zero.');
      return;
    }

    // Produto repetido soma a quantidade em vez de virar duas linhas.
    const atuais = [...this.itens()];
    const existente = atuais.findIndex((i) => i.produtoId === produto.id);

    if (existente >= 0) {
      atuais[existente] = {
        ...atuais[existente],
        quantidade: atuais[existente].quantidade + qtd,
      };
    } else {
      atuais.push({
        produtoId: produto.id,
        codigo: produto.codigo,
        descricao: produto.descricao,
        quantidade: qtd,
      });
    }

    this.itens.set(atuais);
    this.quantidade.set(1);
    this.selecionado.set(null);
    this.buscaProduto.setValue('');
  }

  protected remover(indice: number): void {
    this.itens.set(this.itens().filter((_, i) => i !== indice));
  }

  // ==================================================================
  // Montagem por texto livre
  // ==================================================================

  protected interpretar(): void {
    const texto = this.textoLivre().trim();
    if (!texto || this.interpretando()) return;

    this.interpretando.set(true);
    this.sugestao.set(null);

    this.notasApi
      .interpretar(texto)
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: (resultado) => {
          this.interpretando.set(false);
          this.sugestao.set(resultado);

          if (resultado.itens.length === 0) {
            this.aviso.aviso('Nao consegui identificar nenhum produto nesse texto.');
          }
        },
        error: (erro: ErroApi) => {
          this.interpretando.set(false);
          this.aviso.erro(erro);
        },
      });
  }

  /**
   * A sugestao so entra na nota quando a pessoa manda. Este e o ponto do desenho:
   * a interpretacao propoe, o humano confirma. Nada e gravado sem esse clique.
   */
  protected aceitarSugestao(): void {
    const sugerido = this.sugestao();
    if (!sugerido || sugerido.itens.length === 0) return;

    const atuais = [...this.itens()];

    for (const item of sugerido.itens) {
      const existente = atuais.findIndex((i) => i.produtoId === item.produtoId);

      if (existente >= 0) {
        atuais[existente] = {
          ...atuais[existente],
          quantidade: atuais[existente].quantidade + item.quantidade,
        };
        continue;
      }

      atuais.push({
        produtoId: item.produtoId,
        codigo: item.codigo,
        descricao: item.descricao,
        quantidade: item.quantidade,
      });
    }

    this.itens.set(atuais);
    this.sugestao.set(null);
    this.textoLivre.set('');
    this.aviso.sucesso('Sugestao adicionada. Confira os itens antes de criar a nota.');
  }

  protected descartarSugestao(): void {
    this.sugestao.set(null);
  }

  protected exemplo(): void {
    this.textoLivre.set('3 canetas azuis, 2 cadernos e 1 grampeador');
  }

  protected criar(): void {
    if (this.itens().length === 0) {
      this.aviso.aviso('A nota precisa de pelo menos um produto.');
      return;
    }

    this.salvando.set(true);
    this.notasApi
      .criar(this.itens())
      .pipe(takeUntil(this.destruir$))
      .subscribe({
        next: (nota) => {
          this.salvando.set(false);
          this.aviso.sucesso(`Nota ${nota.numero} criada com status Aberta.`);
          this.router.navigate(['/notas', nota.id]);
        },
        error: (erro: ErroApi) => {
          this.salvando.set(false);
          this.aviso.erro(erro);
        },
      });
  }

  protected totalUnidades(): number {
    return this.itens().reduce((soma, i) => soma + i.quantidade, 0);
  }
}
