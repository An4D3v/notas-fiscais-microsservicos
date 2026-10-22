import { inject, Injectable } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ErroApi } from '../modelos';

/**
 * Mensagens para a pessoa que esta usando o sistema.
 *
 * Regra da casa: a tela nunca mostra "Http failure response" nem codigo de erro cru.
 * Mostra o que aconteceu, o que NAO aconteceu, e o que fazer agora.
 */
@Injectable({ providedIn: 'root' })
export class NotificacaoService {
  private readonly snack = inject(MatSnackBar);

  sucesso(mensagem: string): void {
    this.snack.open(mensagem, 'ok', {
      duration: 4000,
      panelClass: ['aviso-sucesso'],
      horizontalPosition: 'right',
    });
  }

  aviso(mensagem: string): void {
    this.snack.open(mensagem, 'entendi', {
      duration: 7000,
      panelClass: ['aviso-atencao'],
      horizontalPosition: 'right',
    });
  }

  erro(erro: ErroApi | string): void {
    const mensagem = typeof erro === 'string' ? erro : erro.mensagem;
    const duracao = typeof erro !== 'string' && erro.ehIndisponibilidade ? 10000 : 7000;

    this.snack.open(mensagem, 'fechar', {
      duration: duracao,
      panelClass: ['aviso-erro'],
      horizontalPosition: 'right',
    });
  }
}
