using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Issue #91 (achado A08-1): cobre os catches de <c>UsuarioController</c> que dependem de uma
/// <see cref="Microsoft.Data.SqlClient.SqlException"/> real vinda do banco — inalcançáveis no
/// harness InMemory padrão (<see cref="TccApiFactory"/>), porque esse provider não aplica o
/// índice único de e-mail nem foreign keys (P-04 da arquitetura). Usa
/// <see cref="SaveChangesFalhaUsuarioApiFactory"/> + <see cref="SqlExceptionSimulada"/> para
/// fabricar exatamente a exceção que o SQL Server produziria numa corrida real, em vez de trocar
/// o provider de banco da suíte inteira (opção descartada: SQLite não lança
/// <see cref="Microsoft.Data.SqlClient.SqlException"/>, então não exercitaria o "when" real do
/// catch por número de erro).
/// </summary>
public class UsuarioController_FalhasRelacionaisSimuladas_Tests
{
    private const int IdAdmin = 1;
    private const int IdAluno = 10;
    private const int IdOutroAluno = 11;

    private const string SenhaOriginal = "senha-original-123";

    private static readonly string HashAdmin = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-admin");
    private static readonly string HashAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal);
    private static readonly string HashOutroAluno = BCrypt.Net.BCrypt.HashPassword(SenhaOriginal + "-outro");

    private static void Semear(SaveChangesFalhaUsuarioApiFactory factory)
    {
        using var contexto = factory.NovoContextoSemInterceptor();
        contexto.Usuarios.AddRange(
            new Usuario { Id = IdAdmin, Nome = "Admin Teste", Email = "admin@teste.com", SenhaHash = HashAdmin, Tipo = TipoUsuario.Admin, Ativo = true },
            new Usuario { Id = IdAluno, Nome = "Aluno Teste", Email = "aluno@teste.com", SenhaHash = HashAluno, Tipo = TipoUsuario.Aluno, Ativo = true },
            new Usuario { Id = IdOutroAluno, Nome = "Outro Aluno", Email = "outro@teste.com", SenhaHash = HashOutroAluno, Tipo = TipoUsuario.Aluno, Ativo = true });
        contexto.SaveChanges();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // POST /api/usuario — corrida na unicidade de e-mail (pre-check passa, SaveChanges falha)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoIndiceUnico)]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoChaveUnica)]
    public async Task CreateUsuario_CorridaNaUnicidadeDeEmail_Retorna409(int numeroDoErro)
    {
        var sqlException = SqlExceptionSimulada.Criar(numeroDoErro);
        // Guarda contra falso positivo: se a fabricação por reflexão degradar numa
        // atualização do driver, o teste falha aqui — não silenciosamente no catch genérico.
        Assert.Equal(numeroDoErro, sqlException.Number);

        using var factory = new SaveChangesFalhaUsuarioApiFactory(
            new DbUpdateException("violação simulada de índice único", sqlException));
        Semear(factory);

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Nome = "Concorrente",
            Email = "concorrente@teste.com", // nao existe no pre-check, mas a "corrida" falha no SaveChanges
            Senha = "senha-valida-123456",
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PostAsJsonAsync("/api/usuario", dto);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("já está em uso", corpo, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT /api/usuario/{id} — mesma corrida, agora numa entidade Modified
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoIndiceUnico)]
    [InlineData(SqlExceptionSimulada.NumeroViolacaoChaveUnica)]
    public async Task UpdateUsuario_CorridaNaUnicidadeDeEmail_Retorna409(int numeroDoErro)
    {
        var sqlException = SqlExceptionSimulada.Criar(numeroDoErro);
        Assert.Equal(numeroDoErro, sqlException.Number);

        using var factory = new SaveChangesFalhaUsuarioApiFactory(
            new DbUpdateException("violação simulada de índice único", sqlException));
        Semear(factory);

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        var dto = new UsuarioDto
        {
            Id = IdAluno,
            Nome = "Aluno Teste",
            Email = "email-liberado-na-corrida@teste.com", // nao colide no pre-check
            Senha = string.Empty,
            Tipo = TipoUsuario.Aluno,
            Ativo = true
        };

        var response = await client.PutAsJsonAsync($"/api/usuario/{IdAluno}", dto);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("já está em uso", corpo, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DELETE /api/usuario/{id} — backstop generico (violacao de integridade nao prevista
    // pelas checagens explicitas de TCC/banca) — issue #90, achado do qa-agent (Ressalva 4)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteUsuario_FalhaDeIntegridadeReferencialNaoPrevista_Retorna409()
    {
        // Numero generico de violacao de FK no SQL Server (547) — diferente de 2601/2627,
        // prova que o backstop de DeleteUsuario captura QUALQUER SqlException, nao so os
        // dois numeros de unicidade tratados em Create/UpdateUsuario.
        var sqlException = SqlExceptionSimulada.Criar(547);
        Assert.Equal(547, sqlException.Number);

        using var factory = new SaveChangesFalhaUsuarioApiFactory(
            new DbUpdateException("violação simulada de integridade referencial", sqlException));
        Semear(factory);

        var client = factory.CreateClientAutenticado(IdAdmin, "Admin");

        // IdOutroAluno nao orienta TCC nem participa de banca: passa pelas checagens
        // explicitas de UsuarioController.DeleteUsuario e só falha no SaveChanges,
        // simulando um vinculo referencial ainda nao previsto pelo codigo.
        var response = await client.DeleteAsync($"/api/usuario/{IdOutroAluno}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var corpo = await response.Content.ReadAsStringAsync();
        Assert.Contains("existem registros vinculados", corpo, StringComparison.Ordinal);
    }
}
