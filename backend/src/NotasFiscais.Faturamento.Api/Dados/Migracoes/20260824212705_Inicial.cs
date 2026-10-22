using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotasFiscais.Faturamento.Api.Dados.Migracoes
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CriadaEm = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    AtualizadaEm = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ImpressaEm = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChaveImpressao = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UltimoErro = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sequencias",
                columns: table => new
                {
                    Nome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UltimoValor = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sequencias", x => x.Nome);
                });

            migrationBuilder.CreateTable(
                name: "ItensNota",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NotaFiscalId = table.Column<int>(type: "int", nullable: false),
                    ProdutoId = table.Column<int>(type: "int", nullable: false),
                    ProdutoCodigo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProdutoDescricao = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Quantidade = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItensNota", x => x.Id);
                    table.CheckConstraint("CK_ItensNota_Quantidade_Positiva", "[Quantidade] > 0");
                    table.ForeignKey(
                        name: "FK_ItensNota_Notas_NotaFiscalId",
                        column: x => x.NotaFiscalId,
                        principalTable: "Notas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Sequencias",
                columns: new[] { "Nome", "UltimoValor" },
                values: new object[] { "NotaFiscal", 0 });

            migrationBuilder.CreateIndex(
                name: "IX_ItensNota_NotaFiscalId",
                table: "ItensNota",
                column: "NotaFiscalId");

            migrationBuilder.CreateIndex(
                name: "IX_Notas_Numero",
                table: "Notas",
                column: "Numero",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notas_Status",
                table: "Notas",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItensNota");

            migrationBuilder.DropTable(
                name: "Sequencias");

            migrationBuilder.DropTable(
                name: "Notas");
        }
    }
}
