using NotasFiscais.Estoque.Api.Dominio;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Estoque.Api.Dados;

public class EstoqueDbContext(DbContextOptions<EstoqueDbContext> options) : DbContext(options)
{
    public DbSet<Produto> Produtos => Set<Produto>();
    public DbSet<MovimentoEstoque> Movimentos => Set<MovimentoEstoque>();
    public DbSet<MovimentoItem> MovimentoItens => Set<MovimentoItem>();

    /// <summary>
    /// Vale para TODA propriedade DateTime do modelo, sem precisar lembrar de anotar
    /// cada uma. Ver DataUtcConverter para o porque.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        cfg.Properties<DateTime>().HaveConversion<DataUtcConverter>();
        cfg.Properties<DateTime?>().HaveConversion<DataUtcNulavelConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Produto>(e =>
        {
            e.ToTable("Produtos", t =>
                // Cinto e suspensorio: mesmo que um bug passe pela regra da aplicacao,
                // o banco se recusa a gravar saldo negativo.
                t.HasCheckConstraint("CK_Produtos_Saldo_NaoNegativo", "[Saldo] >= 0"));

            e.HasKey(p => p.Id);
            e.Property(p => p.Codigo).HasMaxLength(30).IsRequired();
            e.Property(p => p.Descricao).HasMaxLength(200).IsRequired();
            e.Property(p => p.Saldo).IsRequired();
            e.Property(p => p.CriadoEm).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(p => p.AtualizadoEm).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(p => p.Versao).IsRowVersion();

            // Codigo e a chave de negocio: nao pode repetir.
            e.HasIndex(p => p.Codigo).IsUnique();
        });

        b.Entity<MovimentoEstoque>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.ChaveIdempotencia).HasMaxLength(100).IsRequired();
            e.Property(m => m.RespostaJson).IsRequired();
            e.Property(m => m.OcorridoEm).HasDefaultValueSql("SYSUTCDATETIME()");

            // O CORACAO DA IDEMPOTENCIA. Quem garante a unicidade e o banco, nao um "if" da aplicacao:
            // dois pedidos simultaneos com a mesma chave nao conseguem inserir os dois.
            e.HasIndex(m => m.ChaveIdempotencia).IsUnique();

            e.HasMany(m => m.Itens)
             .WithOne(i => i.Movimento!)
             .HasForeignKey(i => i.MovimentoEstoqueId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MovimentoItem>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.ProdutoCodigo).HasMaxLength(30).IsRequired();
            e.HasIndex(i => i.ProdutoId);
        });
    }
}
