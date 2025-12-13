using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "series",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    titolo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    genere = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    sottogenere = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    periodo_narrativo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    tono_base = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    target = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    lingua = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ambientazione_base = table.Column<string>(type: "TEXT", nullable: true),
                    premessa_serie = table.Column<string>(type: "TEXT", nullable: true),
                    arco_narrativo_serie = table.Column<string>(type: "TEXT", nullable: true),
                    stile_scrittura = table.Column<string>(type: "TEXT", nullable: true),
                    regole_narrative = table.Column<string>(type: "TEXT", nullable: true),
                    note_ai = table.Column<string>(type: "TEXT", nullable: true),
                    episodi_generati = table.Column<int>(type: "INTEGER", nullable: false),
                    data_inserimento = table.Column<DateTime>(type: "TEXT", nullable: false),
                    timestamp = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "series");
        }
    }
}
