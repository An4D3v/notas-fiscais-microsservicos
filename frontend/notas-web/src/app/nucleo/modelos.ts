// Espelho dos contratos das duas APIs. Ficam juntos porque a tela conversa com os dois
// servicos, mas a origem de cada tipo esta anotada para deixar claro quem e dono do que.

// ---------- Servico de Estoque (porta 5001) ----------

export interface Produto {
  id: number;
  codigo: string;
  descricao: string;
  saldo: number;
  criadoEm: string;
  atualizadoEm: string;
}

export interface CriarProduto {
  codigo: string;
  descricao: string;
  saldo: number;
}

export interface AtualizarProduto {
  descricao: string;
  saldo: number;
}

// ---------- Servico de Faturamento (porta 5002) ----------

export type StatusNota = 'Aberta' | 'Processando' | 'Fechada';

export interface ItemNota {
  id: number;
  produtoId: number;
  codigo: string;
  descricao: string;
  quantidade: number;
}

export interface Nota {
  id: number;
  numero: number;
  status: StatusNota;
  criadaEm: string;
  impressaEm: string | null;
  ultimoErro: string | null;
  itens: ItemNota[];
}

export interface ItemNotaEnvio {
  produtoId: number;
  codigo: string;
  descricao: string;
  quantidade: number;
}

export interface SaldoAtualizado {
  produtoId: number;
  codigo: string;
  descricao: string;
  quantidade: number;
  saldoResultante: number;
}

export interface ResultadoImpressao {
  nota: Nota;
  saldosAtualizados: SaldoAtualizado[];
  repetido: boolean;
}

// ---------- Montagem de nota por texto (IA) ----------

export interface ItemSugerido {
  produtoId: number;
  codigo: string;
  descricao: string;
  quantidade: number;
  saldoAtual: number;
  confianca: number;
  trecho: string;
  acimaDoSaldo: boolean;
}

export interface TrechoNaoEntendido {
  trecho: string;
  motivo: string;
}

export interface Interpretacao {
  /** "ia" quando o modelo respondeu, "offline" quando foi o casamento local por texto. */
  modo: 'ia' | 'offline';
  modelo: string | null;
  itens: ItemSugerido[];
  naoEntendidos: TrechoNaoEntendido[];
  aviso: string | null;
}

// ---------- Comum ----------

export interface Pagina<T> {
  itens: T[];
  total: number;
  pagina: number;
  tamanho: number;
}

/**
 * Erro ja traduzido do formato ProblemDetails que as duas APIs devolvem.
 * A tela nunca lida com HttpErrorResponse cru: o interceptor converte para isto.
 */
export interface ErroApi {
  status: number;
  codigo: string;
  mensagem: string;
  detalhes?: unknown;
  /** true quando o problema e indisponibilidade, e nao erro do usuario. */
  ehIndisponibilidade: boolean;
}
