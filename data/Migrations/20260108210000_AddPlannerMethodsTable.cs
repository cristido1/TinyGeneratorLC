using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannerMethodsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE planner_methods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    code TEXT NOT NULL,
                    name TEXT NOT NULL,
                    description TEXT,
                    category TEXT,
                    beat_count INTEGER,
                    structure_schema TEXT,
                    planner_prompt TEXT,
                    validation_rules TEXT,
                    strengths TEXT,
                    weaknesses TEXT,
                    recommended_genres TEXT,
                    supports_series INTEGER NOT NULL DEFAULT 0,
                    notes TEXT,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    RowVersion BLOB
                );
                
                CREATE UNIQUE INDEX IX_planner_methods_code ON planner_methods(code);
                
                INSERT INTO planner_methods (code, name, description, category, beat_count, 
                    structure_schema, planner_prompt, supports_series, is_active)
                VALUES (
                    'SAVE_THE_CAT',
                    'Save the Cat (15 beats)',
                    'Blake Snyder''s 15-beat structure focusing on character transformation and emotional arcs',
                    'planner',
                    15,
                    '{""beats"":[{""beat_name"":""string"",""summary"":""string"",""protagonist_goal"":""string"",""conflict"":""string"",""stakes"":""string"",""tension_level"":1-10}]}',
                    'Genera una struttura narrativa completa seguendo il metodo Save the Cat di Blake Snyder con 15 beat. Ogni beat deve contenere: beat_name, summary (100-150 parole), protagonist_goal, conflict, stakes, tension_level (1-10). Ritorna JSON: {""beats"": [{...}]}',
                    1,
                    1
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS planner_methods;");
        }
    }
}
