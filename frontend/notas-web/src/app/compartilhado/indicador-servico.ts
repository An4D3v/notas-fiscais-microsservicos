import { Component, Input, OnChanges, SimpleChanges, signal } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * Estado de um servico na barra do topo: um ponto e o nome.
 *
 * Este componente existe tambem para demonstrar o ciclo de vida ngOnChanges:
 * ele nao guarda estado proprio de negocio, ele REAGE a mudanca do @Input.
 * Quando o servico cai ou volta, a cor e o texto sao recalculados aqui,
 * e nao espalhados pelo template do pai.
 */
@Component({
  selector: 'nf-indicador-servico',
  imports: [MatTooltipModule],
  template: `
    <span class="ind" [class]="classe()" [matTooltip]="dica()">
      <span class="k-ponto"></span>
      {{ nome }}
    </span>
  `,
  styles: `
    .ind {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      font-size: 12.5px;
      font-weight: 500;
      color: var(--k-ink-2);
      white-space: nowrap;
    }
    .k-ponto {
      background: var(--k-ink-4);
    }
    /* No ar e o estado normal: o ponto e discreto, quase nao se nota.
       Interface boa nao comemora o funcionamento esperado. */
    .no-ar .k-ponto {
      background: #22a06b;
    }
    /* Fora do ar precisa saltar. Cor forte, halo e pulso. */
    .fora {
      color: var(--k-danger);
      font-weight: 560;
    }
    .fora .k-ponto {
      background: var(--k-danger);
      box-shadow: 0 0 0 3px rgba(180, 40, 60, 0.16);
      animation: pulsar 1.15s ease-in-out infinite;
    }
    @keyframes pulsar {
      50% {
        opacity: 0.32;
      }
    }
  `,
})
export class IndicadorServico implements OnChanges {
  /** Nome curto exibido ao lado do ponto. */
  @Input({ required: true }) nome!: string;

  /** null = ainda verificando, true = no ar, false = fora do ar. */
  @Input({ required: true }) noAr!: boolean | null;

  protected readonly classe = signal('verificando');
  protected readonly dica = signal('Verificando...');

  ngOnChanges(mudancas: SimpleChanges): void {
    if (!('noAr' in mudancas)) return;

    if (this.noAr === null) {
      this.classe.set('verificando');
      this.dica.set(`Verificando o servico de ${this.nome}...`);
      return;
    }

    if (this.noAr) {
      this.classe.set('no-ar');
      this.dica.set(`Servico de ${this.nome} respondendo normalmente.`);
      return;
    }

    this.classe.set('fora');
    this.dica.set(
      `Servico de ${this.nome} nao esta respondendo. As operacoes que dependem dele vao falhar com aviso.`,
    );
  }
}
