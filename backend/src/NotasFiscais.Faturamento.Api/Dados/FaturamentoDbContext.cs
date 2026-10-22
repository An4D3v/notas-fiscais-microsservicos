using NotasFiscais.Faturamento.Api.Dominio;
using Microsoft.EntityFrameworkCore;

namespace NotasFiscais.Faturamento.Api.Dados;

public class FaturamentoDbContext(DbContextOptions<FaturamentoDbContext> options) : DbContext(options)
{
    public DbSet<NotaFiscal> Notas => Set<NotaFiscal>();
    public DbSet<ItemNota> ItensNota => Set<ItemNota>();
    public DbSet<Sequencia> Sequencias => Set<Sequencia>();

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
        b.Entity<NotaFiscal>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Numero).IsRequired();
            e.Property(n => n.Status).HasConversion<int>().IsRequired();
            e.Property(n => n.ChaveImpressao).HasMaxLength(100);
            e.Property(n => n.UltimoErro).HasMaxLength(500);
            e.Property(n => n.CriadaEm).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(n => n.AtualizadaEm).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(n => n.Versao).IsRowVersion();

            e.HasIndex(n => n.Numero).IsUnique();
            e.HasIndex(n => n.Status);

            e.HasMany(n => n.Itens)
             .WithOne(i => i.Nota!)
             .HasForeignKey(i => i.NotaFiscalId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ItemNota>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.ProdutoCodigo).HasMaxLength(30).IsRequired();
            e.Property(i => i.ProdutoDescricao).HasMaxLength(200).IsRequired();
            e.ToTable("ItensNota", t =>
                t.HasCheckConstraint("CK_ItensNota_Quantidade_Positiva", "[Quantidade] > 0"));
        });

        b.Entity<Sequencia>(e =>
        {
            e.HasKey(s => s.Nome);
            e.Property(s => s.Nome).HasMaxLength(50);

            // A linha do contador ja nasce com o banco: nao existe "primeira nota especial".
            e.HasData(new Sequencia { Nome = "NotaFiscal", UltimoValor = 0 });
        });
    }
}
