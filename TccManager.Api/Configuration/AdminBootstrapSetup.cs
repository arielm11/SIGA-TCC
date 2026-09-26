using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using TccManager.Api.Data;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Shared.Validation;

namespace TccManager.Api.Configuration;

/// <summary>
/// Issue #88 (D1-D5): bootstrap do primeiro Admin via configuração de startup (Opção C) —
/// único mecanismo de criação de Admin fora da aplicação. O mecanismo de migration (Opção A)
/// foi tecnicamente rejeitado (ver docs/arquitetura/2026-09-03-bootstrap-primeiro-admin.md,
/// seção 4): sob <c>dotnet ef migrations script</c>, a credencial vazaria para um arquivo SQL
/// compartilhado/log de build.
///
/// Chamado uma única vez, entre <c>builder.Build()</c> e o pipeline HTTP, aguardado (await) —
/// garante que a API não começa a atender requisições com o bootstrap em voo.
/// </summary>
public static class AdminBootstrapSetup
{
    private const int NomeMaxLength = 200;
    private const int EmailMaxLength = 450;
    private const string NomePadrao = "Administrador";

    public static async Task ExecutarBootstrapAdminAsync(this WebApplication app)
    {
        var auditLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("TccManager.Api.Auditoria");

        var email = app.Configuration["Admin:BootstrapEmail"];
        var senha = app.Configuration["Admin:BootstrapSenha"];
        var nome = app.Configuration["Admin:BootstrapNome"];

        // (1) Nada configurado -> caminho absolutamente silencioso e sem I/O. Essencial para
        // não impactar ambientes/testes sem essa configuração (TccApiFactory não a define).
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(senha))
            return;

        // (1b) Meia configuração é erro de operador, não "desligado".
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha))
        {
            auditLogger.LogError(
                "Bootstrap de Admin recusado. Motivo: {Motivo} — Admin:BootstrapEmail e Admin:BootstrapSenha precisam ser configurados juntos.",
                "ConfiguracaoIncompleta");
            return;
        }

        if (string.IsNullOrWhiteSpace(nome))
            nome = NomePadrao;
        else if (nome.Length > NomeMaxLength)
            nome = nome[..NomeMaxLength];

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // (2) Pré-condição de negócio (RF-02): só age com zero Admins ativos. Nunca
            // promove/reativa uma conta existente (D4) — só cria uma nova.
            var adminsAtivos = await db.ContarAdminsAtivosAsync();
            if (adminsAtivos > 0)
            {
                auditLogger.LogWarning(
                    "Configuração de bootstrap de Admin presente, mas o sistema já tem {TotalAdminsAtivos} Admin(s) ativo(s): nenhuma ação. REMOVA Admin:BootstrapEmail/Admin:BootstrapSenha do ambiente.",
                    adminsAtivos);
                return;
            }

            // (3) Validação da credencial — antes de qualquer escrita.
            if (!EmailValido(email))
            {
                auditLogger.LogError("Bootstrap de Admin recusado. Motivo: {Motivo}", "EmailInvalido");
                return;
            }

            if (!PoliticaSenha.Valida(senha, email, out var motivoSenha))
            {
                auditLogger.LogError(
                    "Bootstrap de Admin recusado. Motivo: {Motivo} ({DetalhePolitica})",
                    "SenhaForaDaPolitica",
                    motivoSenha);
                return;
            }

            // (4) O e-mail não pode pertencer a ninguém (D4).
            if (await db.Usuarios.AnyAsync(u => u.Email == email))
            {
                auditLogger.LogError("Bootstrap de Admin recusado. Motivo: {Motivo}", "EmailJaEmUso");
                return;
            }

            // (5) Criação.
            var novoAdmin = new Usuario
            {
                Nome = nome,
                Email = email,
                SenhaHash = BCrypt.Net.BCrypt.HashPassword(senha),
                Tipo = TipoUsuario.Admin,
                Ativo = true,
                PrecisaTrocarSenha = true
            };

            db.Usuarios.Add(novoAdmin);
            await db.SaveChangesAsync();

            // RF-05 (A1): evento que, em produção estabelecida, nunca deveria aparecer.
            // Warning garante visibilidade mesmo se o override de categoria for removido.
            // Nunca loga e-mail/senha/hash — só o id gerado e o mecanismo.
            auditLogger.LogWarning(
                "BOOTSTRAP DE ADMIN EXECUTADO: usuário {UsuarioId} criado. Mecanismo: {Mecanismo}.",
                novoAdmin.Id,
                "configuracao-startup");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Corrida entre instâncias subindo ao mesmo tempo: a perdedora recebe a
            // violação do índice único de Email (IX_usuarios_Email) — outra instância já
            // criou o mesmo Admin. Não é erro. Não vincular a variável de exceção (LGPD): a
            // mensagem do provedor relacional carrega o e-mail duplicado.
            auditLogger.LogWarning("Bootstrap de Admin concorrente: outra instância já criou o Admin.");
        }
        catch (Exception)
        {
            // D5: falha de bootstrap não derruba a API — é uma tarefa operacional opcional,
            // não uma dependência de runtime. Não vincular a variável de exceção (LGPD).
            auditLogger.LogError("Falha ao executar o bootstrap de Admin; a API seguirá subindo.");
        }
    }

    // Mesma regra de UsuarioDtoValidator para UsuarioDto.Email: EmailAddress() do FluentValidation
    // (um único '@', nem no início nem no fim) + MailboxAddress.TryParse (MimeKit, que sozinho
    // aceita endereço sem '@') + teto de 450 caracteres alinhado a Usuario.Email.
    private static bool EmailValido(string email)
    {
        var indiceArroba = email.IndexOf('@');

        return email.Length <= EmailMaxLength
            && indiceArroba > 0
            && indiceArroba < email.Length - 1
            && indiceArroba == email.LastIndexOf('@')
            && MailboxAddress.TryParse(email, out _);
    }
}
