using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração do endpoint GET /api/avaliador/meus-convites (issue #69, correção pós-revisão
/// de segurança A01-1): ArquivoFinalCaminho (string, caminho estático) foi substituído por
/// ArquivoFinalEntregaId (int?) + ArquivoFinalExtensao (string?), para o Client baixar via o
/// endpoint autenticado GET /api/tcc/entregas/{id}/download em vez de um link direto.
///
/// A projeção do controller busca o caminho da Entrega Final em SQL e só deriva a extensão
/// (Path.GetExtension) depois de materializar em memória — este teste é o que garante que
/// essa projeção em duas etapas continua funcionando e que a extensão devolvida é a real do
/// arquivo, não uma assumida (bug corrigido: o Client chegou a fixar ".pdf" para toda
/// entrega Final, mesmo quando o arquivo era .docx/.doc/.zip).
/// </summary>
public class AvaliadorController_MeusConvites_Tests
{
    private sealed record Semeadura(int AvaliadorId, int TccComEntregaFinalId, int TccSemEntregaFinalId);

    private static async Task<Semeadura> SemearAsync(WebRootIsolatedApiFactory factory)
    {
        using var context = factory.CriarContextoDireto();

        var aluno1 = new Usuario { Nome = "Aluno 1", Email = "aluno1@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var aluno2 = new Usuario { Nome = "Aluno 2", Email = "aluno2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true };
        var avaliador = new Usuario { Nome = "Avaliador", Email = "aval@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true };
        context.Usuarios.AddRange(aluno1, aluno2, avaliador);
        await context.SaveChangesAsync();

        var tccComFinal = new Tcc { Titulo = "TCC com Final", Resumo = "r", AlunoId = aluno1.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
        var tccSemFinal = new Tcc { Titulo = "TCC sem Final", Resumo = "r", AlunoId = aluno2.Id, Status = StatusTcc.AguardandoDefesa, DataCriacao = DateTime.UtcNow };
        context.Tccs.AddRange(tccComFinal, tccSemFinal);
        await context.SaveChangesAsync();

        var bancaComFinal = new Banca { TccId = tccComFinal.Id, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala 1" };
        var bancaSemFinal = new Banca { TccId = tccSemFinal.Id, DataHora = DateTime.UtcNow.AddDays(2), Local = "Sala 2" };
        context.Banca.AddRange(bancaComFinal, bancaSemFinal);
        await context.SaveChangesAsync();

        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = bancaComFinal.Id, ProfessorId = avaliador.Id });
        context.BancaAvaliadores.Add(new BancaAvaliador { BancaId = bancaSemFinal.Id, ProfessorId = avaliador.Id });
        await context.SaveChangesAsync();

        // Entrega Final em .docx, de propósito: o bug corrigido assumia .pdf sempre.
        context.Entregas.Add(new Entrega
        {
            TccId = tccComFinal.Id,
            Titulo = "Versão Final",
            ArquivoCaminho = "/uploads/entregas/guid-qualquer_versao-final.docx",
            Tipo = TipoEntrega.Final,
            DataEnvio = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return new Semeadura(avaliador.Id, tccComFinal.Id, tccSemFinal.Id);
    }

    [Fact]
    public async Task ConviteComEntregaFinal_TrazEntregaIdEExtensaoReaisDoArquivo()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");

        var convites = await client.GetFromJsonAsync<List<ConviteBancaDto>>("/api/avaliador/meus-convites");

        var convite = Assert.Single(convites!, c => c.TccTitulo == "TCC com Final");
        Assert.NotNull(convite.ArquivoFinalEntregaId);
        Assert.Equal(".docx", convite.ArquivoFinalExtensao);
    }

    [Fact]
    public async Task ConviteSemEntregaFinal_NaoTrazEntregaIdNemExtensao()
    {
        using var factory = new WebRootIsolatedApiFactory();
        var s = await SemearAsync(factory);
        var client = factory.CreateClientAutenticado(s.AvaliadorId, "Professor");

        var convites = await client.GetFromJsonAsync<List<ConviteBancaDto>>("/api/avaliador/meus-convites");

        var convite = Assert.Single(convites!, c => c.TccTitulo == "TCC sem Final");
        Assert.Null(convite.ArquivoFinalEntregaId);
        Assert.Null(convite.ArquivoFinalExtensao);
    }
}
