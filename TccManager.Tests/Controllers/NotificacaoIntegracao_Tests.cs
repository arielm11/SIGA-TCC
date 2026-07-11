using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TccManager.Api.Services.Email;
using TccManager.Shared.DTOs;
using TccManager.Shared.Enums;
using TccManager.Shared.Models;
using TccManager.Tests.Services.Email;
using Xunit;

namespace TccManager.Tests.Controllers;

/// <summary>
/// Integração leve dos pontos de disparo de notificação: substitui o IEmailQueue real
/// por um FakeEmailQueue e confirma, através do pipeline HTTP completo, que (a) a resposta
/// HTTP continua correta mesmo sem SMTP configurado e (b) a mensagem certa é enfileirada
/// dentro do request (destinatários resolvidos do banco). Reaproveita o mesmo host único
/// de TccApiFactory (mesma InstanceDb usada para seed e para o request).
/// </summary>
public class NotificacaoIntegracao_Tests
{
    private const int idCoordenador = 1;
    private const int idAluno = 10;
    private const int idOrientador = 20;
    private const int idAvaliador1 = 21;
    private const int idAvaliador2 = 22;

    private sealed class FactoryComFilaFake : TccApiFactory
    {
        private readonly FakeEmailQueue _fila;

        public FactoryComFilaFake(FakeEmailQueue fila) => _fila = fila;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailQueue>();
                services.AddSingleton<IEmailQueue>(_fila);
            });
        }
    }

    [Fact]
    public async Task AgendarBanca_RetornaSucesso_EEnfileiraEmailBancaAgendadaComTodosOsDestinatarios()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                new Usuario { Id = idAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = idOrientador, Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = idAvaliador1, Nome = "Avaliador Um", Email = "aval1@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = idAvaliador2, Nome = "Avaliador Dois", Email = "aval2@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true });
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.AguardandoDefesa,
                DataCriacao = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");
        var dto = new AgendarBancaDto
        {
            DataHora = DateTime.Now.AddDays(7),
            Local = "Sala 101",
            ProfessoresIds = new List<int> { idAvaliador1, idAvaliador2 },
            MembrosExternosIds = new List<int>()
        };

        var response = await client.PostAsJsonAsync("/api/coordenador/tcc/1/banca", dto);

        response.EnsureSuccessStatusCode();

        // Uma EmailMessage por destinatário (nunca todos no mesmo To:).
        Assert.All(fila.Mensagens, m => Assert.Equal("Banca de TCC agendada", m.Assunto));
        var destinatarios = DestinatariosDaFila(fila);
        Assert.Equal(4, destinatarios.Count);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.Contains("aval1@teste.com", destinatarios);
        Assert.Contains("aval2@teste.com", destinatarios);
    }

    [Fact]
    public async Task RegistrarResultadoBanca_Reprovado_EnfileiraEmailIncluindoCoordenadorAtivo()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                new Usuario { Id = idAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = idOrientador, Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = 30, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.AguardandoDefesa,
                DataCriacao = DateTime.UtcNow
            });
            ctx.Entregas.Add(new Entrega { TccId = 1, Titulo = "Versão Final", ArquivoCaminho = "/x.pdf", Tipo = TipoEntrega.Final, DataEnvio = DateTime.UtcNow });
            ctx.Banca.Add(new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala" });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var form = new MultipartFormDataContent
        {
            { new StringContent((40.0m).ToString(CultureInfo.InvariantCulture)), "notaFinal" },
            { new StringContent("Não atingiu a nota mínima."), "motivoReprovacao" }
        };
        var pdfFake = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        pdfFake.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdfFake, "arquivoAta", "ata.pdf");

        var response = await client.PostAsync("/api/coordenador/banca/1/registrar-resultado", form);

        response.EnsureSuccessStatusCode();

        Assert.All(fila.Mensagens, m => Assert.Equal("Resultado final da banca: reprovado", m.Assunto));
        var destinatarios = DestinatariosDaFila(fila);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.Contains("coord@teste.com", destinatarios);
    }

    [Fact]
    public async Task RegistrarResultadoBanca_Aprovado_NaoIncluiCoordenadorNosDestinatarios()
    {
        var fila = new FakeEmailQueue();
        using var factory = new FactoryComFilaFake(fila);

        using (var ctx = factory.CriarContextoDireto())
        {
            ctx.Usuarios.AddRange(
                new Usuario { Id = idAluno, Nome = "Aluno", Email = "aluno@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Aluno, Ativo = true },
                new Usuario { Id = idOrientador, Nome = "Orientador", Email = "orient@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Professor, Ativo = true },
                new Usuario { Id = 30, Nome = "Coordenador", Email = "coord@teste.com", SenhaHash = "x", Tipo = TipoUsuario.Coordenador, Ativo = true });
            ctx.Tccs.Add(new Tcc
            {
                Id = 1,
                Titulo = "TCC de Teste",
                Resumo = "Resumo",
                AlunoId = idAluno,
                OrientadorId = idOrientador,
                Status = StatusTcc.AguardandoDefesa,
                DataCriacao = DateTime.UtcNow
            });
            ctx.Banca.Add(new Banca { Id = 1, TccId = 1, DataHora = DateTime.UtcNow.AddDays(1), Local = "Sala" });
            await ctx.SaveChangesAsync();
        }

        var client = factory.CreateClientAutenticado(idCoordenador, "Coordenador");

        var form = new MultipartFormDataContent
        {
            { new StringContent((85.0m).ToString(CultureInfo.InvariantCulture)), "notaFinal" }
        };
        var pdfFake = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        pdfFake.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdfFake, "arquivoAta", "ata.pdf");

        var response = await client.PostAsync("/api/coordenador/banca/1/registrar-resultado", form);

        response.EnsureSuccessStatusCode();

        Assert.All(fila.Mensagens, m => Assert.Equal("Resultado final da banca: aprovado", m.Assunto));
        var destinatarios = DestinatariosDaFila(fila);
        Assert.Contains("aluno@teste.com", destinatarios);
        Assert.Contains("orient@teste.com", destinatarios);
        Assert.DoesNotContain("coord@teste.com", destinatarios);
    }

    /// <summary>
    /// Achata os destinatários da fila garantindo o novo contrato de privacidade: uma
    /// EmailMessage por destinatário, cada uma com exatamente 1 e-mail no campo To:.
    /// </summary>
    private static List<string> DestinatariosDaFila(FakeEmailQueue fila)
        => fila.Mensagens.Select(m => Assert.Single(m.Destinatarios)).ToList();
}
