using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesFinalGoalAndEpisodeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "episode_goal",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "initial_phase",
                table: "series_episodes",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "start_situation",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serie_final_goal",
                table: "series",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "episode_goal",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "initial_phase",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "start_situation",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "serie_final_goal",
                table: "series");
        }
    }
}
