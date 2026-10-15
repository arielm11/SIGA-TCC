using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TccManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMaxLengthCamposTextoLivre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Achado SonarCloud (csharpsquid:S8747, 2026-10-11): ALTER COLUMN estreitando
            // nvarchar(max) para um tamanho fixo falha (SQL Server) se alguma linha já
            // existente tiver um valor mais longo que o novo limite — a validação do lado da
            // aplicação (FluentValidation) só vale para escritas feitas depois dela existir,
            // não para dados já persistidos antes desta migration. Cada UPDATE abaixo trunca
            // só o que exceder o novo limite (LEN/LEFT contam caracteres, não bytes — correto
            // para nvarchar), tornando o ALTER COLUMN seguro em qualquer banco, incluindo um
            // já semeado antes deste limite existir.
            migrationBuilder.Sql("UPDATE [usuarios] SET [Nome] = LEFT([Nome], 200) WHERE LEN([Nome]) > 200;");
            migrationBuilder.AlterColumn<string>(
                name: "Nome",
                table: "usuarios",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Tccs] SET [Titulo] = LEFT([Titulo], 200) WHERE LEN([Titulo]) > 200;");
            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "Tccs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Tccs] SET [Resumo] = LEFT([Resumo], 4000) WHERE LEN([Resumo]) > 4000;");
            migrationBuilder.AlterColumn<string>(
                name: "Resumo",
                table: "Tccs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Tccs] SET [MotivoRejeicao] = LEFT([MotivoRejeicao], 2000) WHERE LEN([MotivoRejeicao]) > 2000;");
            migrationBuilder.AlterColumn<string>(
                name: "MotivoRejeicao",
                table: "Tccs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.Sql("UPDATE [MembrosExternos] SET [Nome] = LEFT([Nome], 200) WHERE LEN([Nome]) > 200;");
            migrationBuilder.AlterColumn<string>(
                name: "Nome",
                table: "MembrosExternos",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [MembrosExternos] SET [Instituicao] = LEFT([Instituicao], 300) WHERE LEN([Instituicao]) > 300;");
            migrationBuilder.AlterColumn<string>(
                name: "Instituicao",
                table: "MembrosExternos",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [MembrosExternos] SET [Email] = LEFT([Email], 450) WHERE LEN([Email]) > 450;");
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "MembrosExternos",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Entregas] SET [Titulo] = LEFT([Titulo], 200) WHERE LEN([Titulo]) > 200;");
            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "Entregas",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Entregas] SET [Feedback] = LEFT([Feedback], 2000) WHERE LEN([Feedback]) > 2000;");
            migrationBuilder.AlterColumn<string>(
                name: "Feedback",
                table: "Entregas",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.Sql("UPDATE [Banca] SET [Local] = LEFT([Local], 300) WHERE LEN([Local]) > 300;");
            migrationBuilder.AlterColumn<string>(
                name: "Local",
                table: "Banca",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql("UPDATE [Acompanhamentos] SET [Ata] = LEFT([Ata], 4000) WHERE LEN([Ata]) > 4000;");
            migrationBuilder.AlterColumn<string>(
                name: "Ata",
                table: "Acompanhamentos",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Nome",
                table: "usuarios",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "Tccs",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Resumo",
                table: "Tccs",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000);

            migrationBuilder.AlterColumn<string>(
                name: "MotivoRejeicao",
                table: "Tccs",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Nome",
                table: "MembrosExternos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Instituicao",
                table: "MembrosExternos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "MembrosExternos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "Titulo",
                table: "Entregas",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Feedback",
                table: "Entregas",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Local",
                table: "Banca",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<string>(
                name: "Ata",
                table: "Acompanhamentos",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000);
        }
    }
}
