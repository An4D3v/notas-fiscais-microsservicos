import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { catchError, of } from 'rxjs';
import { Produto } from '../../nucleo/modelos';
import { ProdutosService } from '../../nucleo/servicos/produtos.service';

export interface DadosFormularioProduto {
  produto?: Produto;
}

/**
 * Formulário de produto em janela, usado tanto para criar quanto para editar.
 *
 * Formulários reativos: a validação vive no TypeScript, não no HTML, o que deixa
 * a regra testável e evita validação duplicada em dois lugares. O código não pode
 * ser trocado depois de criado, então no modo edição o campo é desabilitado em vez
 * de escondido: a pessoa continua vendo qual produto está mexendo.
 */
@Component({
  selector: 'nf-produto-formulario',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
  ],
  template: `
    <h2 mat-dialog-title>{{ editando ? 'Editar produto' : 'Novo produto' }}</h2>

    <mat-dialog-content>
      <form [formGroup]="form" class="form" (ngSubmit)="salvar()">
        <mat-form-field appearance="outline">
          <mat-label>Código</mat-label>
          <input
            matInput
            formControlName="codigo"
            maxlength="30"
            autocomplete="off"
            [placeholder]="sugestao() || 'P-007'"
            (keydown.tab)="aceitarSugestao()"
          />

          @if (form.controls.codigo.hasError('required') && form.controls.codigo.touched) {
            <mat-error>Informe o código do produto.</mat-error>
          } @else if (editando) {
            <mat-hint>O código não muda depois que o produto é criado.</mat-hint>
          }
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Descrição</mat-label>
          <input matInput formControlName="descricao" placeholder="Caneta azul" maxlength="200" />
          @if (form.controls.descricao.hasError('required') && form.controls.descricao.touched) {
            <mat-error>Informe a descrição.</mat-error>
          }
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>Saldo em estoque</mat-label>
          <input matInput type="number" formControlName="saldo" min="0" />
          @if (form.controls.saldo.hasError('min')) {
            <mat-error>O saldo não pode ser negativo.</mat-error>
          }
        </mat-form-field>
      </form>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancelar</button>
      <button matButton="filled" [disabled]="form.invalid" (click)="salvar()">Salvar</button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-width: 350px;
      padding-top: 8px;
    }
  `,
})
export class ProdutoFormulario implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly ref = inject(MatDialogRef<ProdutoFormulario>);
  private readonly produtos = inject(ProdutosService);
  private readonly dados = inject<DadosFormularioProduto>(MAT_DIALOG_DATA);

  protected readonly editando = !!this.dados?.produto;

  /**
   * Próximo código livre da sequência. Vazio quando não deu para deduzir.
   *
   * Aparece só como placeholder, sem texto de ajuda embaixo do campo. Uma linha
   * explicando um atalho que ninguém pediu é ruído permanente para todo mundo,
   * em troca de ensinar uma tecla. Quem apertar Tab ganha o preenchimento;
   * quem não apertar nem percebe que existia.
   */
  protected readonly sugestao = signal('');

  protected readonly form = this.fb.nonNullable.group({
    codigo: [
      { value: this.dados?.produto?.codigo ?? '', disabled: this.editando },
      [Validators.required, Validators.maxLength(30)],
    ],
    descricao: [
      this.dados?.produto?.descricao ?? '',
      [Validators.required, Validators.maxLength(200)],
    ],
    saldo: [this.dados?.produto?.saldo ?? 0, [Validators.required, Validators.min(0)]],
  });

  ngOnInit(): void {
    if (this.editando) return;

    // Lê o cadastro para descobrir onde a numeração parou. Se a chamada falhar,
    // a sugestão simplesmente não aparece: é conveniência, não requisito, e não
    // pode impedir alguém de cadastrar um produto.
    this.produtos
      .listar('', 1, 200)
      .pipe(catchError(() => of(null)))
      .subscribe((pagina) => {
        if (!pagina) return;
        this.sugestao.set(proximoCodigo(pagina.itens.map((p) => p.codigo)));
      });
  }

  /**
   * Tab com o campo vazio aceita a sugestão. O Tab continua andando para o
   * próximo campo normalmente: quem navega por teclado não perde a tecla.
   */
  protected aceitarSugestao(): void {
    if (this.form.controls.codigo.value || !this.sugestao()) return;
    this.form.controls.codigo.setValue(this.sugestao());
  }

  protected salvar(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    // getRawValue inclui os campos desabilitados (o código, no modo edição).
    this.ref.close(this.form.getRawValue());
  }
}

/**
 * Deduz o próximo código a partir dos que já existem.
 *
 * Considera só os que seguem o padrão PREFIXO-NÚMERO (P-006), pega o maior de
 * cada prefixo e soma um, preservando os zeros à esquerda: P-009 vira P-010, e
 * não P-10. Se o cadastro estiver vazio ou não houver nenhum código no padrão,
 * devolve vazio e a tela não sugere nada.
 */
export function proximoCodigo(codigos: readonly string[]): string {
  const padrao = /^([A-Za-z]+)-(\d+)$/;

  let melhor: { prefixo: string; numero: number; digitos: number } | null = null;

  for (const codigo of codigos) {
    const casou = padrao.exec(codigo.trim());
    if (!casou) continue;

    const numero = Number(casou[2]);
    if (!melhor || numero > melhor.numero) {
      melhor = { prefixo: casou[1].toUpperCase(), numero, digitos: casou[2].length };
    }
  }

  if (!melhor) return '';

  const proximo = melhor.numero + 1;
  return `${melhor.prefixo}-${String(proximo).padStart(melhor.digitos, '0')}`;
}
