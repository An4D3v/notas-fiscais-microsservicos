import { Component, Input } from '@angular/core';
import { StatusNota } from '../nucleo/modelos';

/**
 * Status da nota: um ponto e a palavra.
 *
 * Trocado de pastilha preenchida para ponto + texto de proposito. Numa lista
 * de vinte notas, vinte pastilhas coloridas viram um mosaico e o olho para de
 * distinguir uma da outra. O ponto informa igual e devolve o silencio a tela.
 */
@Component({
  selector: 'nf-selo-status',
  template: `
    <span class="selo" [class]="'s-' + status.toLowerCase()">
      <span class="k-ponto"></span>{{ status }}
    </span>
  `,
  styles: `
    .selo {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      font-size: 12.5px;
      font-weight: 560;
      color: var(--k-ink-2);
      white-space: nowrap;
    }
    .s-aberta .k-ponto {
      background: #c98a00;
    }
    .s-processando .k-ponto {
      background: #5a68c9;
      animation: piscar 1.3s ease-in-out infinite;
    }
    .s-fechada .k-ponto {
      background: #22a06b;
    }
    @keyframes piscar {
      50% {
        opacity: 0.3;
      }
    }
  `,
})
export class SeloStatus {
  @Input({ required: true }) status!: StatusNota;
}
