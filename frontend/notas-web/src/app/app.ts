import { Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { IndicadorServico } from './compartilhado/indicador-servico';
import { EstadoDeCarregamento } from './nucleo/interceptores';
import { SaudeService } from './nucleo/servicos/saude.service';

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatIconModule,
    MatProgressBarModule,
    IndicadorServico,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  private readonly carregamento = inject(EstadoDeCarregamento);
  protected readonly saude = inject(SaudeService);

  /** Maior que zero quando existe alguma requisicao em andamento. */
  protected readonly ocupado = this.carregamento.carregando;
}
