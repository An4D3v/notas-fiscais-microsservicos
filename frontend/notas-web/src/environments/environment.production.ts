// Usado no build de produção (o que vai para a Vercel): os dois serviços no Azure App Service.
export const environment = {
  producao: true,
  estoqueUrl: 'https://notas-estoque-ana.azurewebsites.net',
  faturamentoUrl: 'https://notas-faturamento-ana.azurewebsites.net',
};
