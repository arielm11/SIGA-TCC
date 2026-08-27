using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TccManager.Api.Services.Notifications;
using Xunit;

namespace TccManager.Tests.Services.Notifications;

/// <summary>
/// Testes unitários de FileEmailTemplateRenderer. Os templates são embedded resources
/// na DLL de TccManager.Api (referenciada pelo projeto de teste), então o renderer real
/// os carrega sem depender de path relativo. Aqui exercitamos a substituição de
/// placeholders {{Chave}} contra os templates reais dos 7 eventos.
/// </summary>
public class FileEmailTemplateRendererTests
{
    private readonly FileEmailTemplateRenderer _renderer = new(NullLogger<FileEmailTemplateRenderer>.Instance);

    [Fact]
    public void Render_SubstituiTodosOsPlaceholdersDoTemplate()
    {
        var valores = new Dictionary<string, string>
        {
            ["NomeAluno"] = "Maria Silva",
            ["TituloTcc"] = "Análise de Redes Neurais",
            ["NomeOrientador"] = "Prof. João Souza"
        };

        var corpo = _renderer.Render("proposta-aprovada", valores);

        Assert.Contains("Maria Silva", corpo);
        Assert.Contains("Análise de Redes Neurais", corpo);
        Assert.Contains("Prof. João Souza", corpo);
        // Nenhum dos placeholders resolvidos deve permanecer no corpo final.
        Assert.DoesNotContain("{{NomeAluno}}", corpo);
        Assert.DoesNotContain("{{TituloTcc}}", corpo);
        Assert.DoesNotContain("{{NomeOrientador}}", corpo);
    }

    [Fact]
    public void Render_SubstituiPlaceholderQueApareceMaisDeUmaVez()
    {
        // banca-agendada usa {{TituloTcc}} e {{NomeAluno}}; garantimos que a chave
        // "TituloTcc" (referenciada uma vez no corpo) é substituída e o marcador some.
        var valores = new Dictionary<string, string>
        {
            ["NomeAluno"] = "Ana",
            ["TituloTcc"] = "Titulo X",
            ["DataHora"] = "15/08/2026 14:30",
            ["Local"] = "Sala 101",
            ["ListaMembrosBanca"] = "<li>Fulano</li><li>Ciclano</li>"
        };

        var corpo = _renderer.Render("banca-agendada", valores);

        Assert.DoesNotContain("{{TituloTcc}}", corpo);
        Assert.DoesNotContain("{{ListaMembrosBanca}}", corpo);
        Assert.Contains("15/08/2026 14:30", corpo);
        Assert.Contains("Sala 101", corpo);
        // O fragmento HTML intencional da lista deve entrar sem escape.
        Assert.Contains("<li>Fulano</li><li>Ciclano</li>", corpo);
    }

    [Fact]
    public void Render_PlaceholderNaoInformado_PermaneceLiteralNoCorpo()
    {
        // O renderer só troca as chaves presentes no dicionário; chaves ausentes
        // ficam intactas. Documenta o contrato de substituição (não lança, não limpa).
        var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Só o nome"
        });

        Assert.Contains("Só o nome", corpo);
        Assert.Contains("{{TituloTcc}}", corpo);
        Assert.Contains("{{NomeOrientador}}", corpo);
    }

    [Fact]
    public void Render_ChaveExtraNaoPresenteNoTemplate_NaoAfetaResultado()
    {
        var corpo = _renderer.Render("aceite-final", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Aluno",
            ["TituloTcc"] = "Titulo",
            ["ChaveInexistente"] = "ignorada"
        });

        Assert.DoesNotContain("ignorada", corpo);
        Assert.DoesNotContain("{{NomeAluno}}", corpo);
        Assert.DoesNotContain("{{TituloTcc}}", corpo);
    }

    [Theory]
    [InlineData("proposta-aprovada")]
    [InlineData("proposta-rejeitada")]
    [InlineData("banca-agendada")]
    [InlineData("feedback-registrado")]
    [InlineData("aceite-final")]
    [InlineData("resultado-aprovado")]
    [InlineData("resultado-reprovado")]
    public void Render_CadaUmDos7Templates_CarregaComSucesso(string chave)
    {
        var corpo = _renderer.Render(chave, new Dictionary<string, string>());

        Assert.False(string.IsNullOrWhiteSpace(corpo));
        Assert.Contains("<html", corpo);
    }

    [Fact]
    public void Render_TemplateInexistente_LancaInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => _renderer.Render("template-que-nao-existe", new Dictionary<string, string>()));

        Assert.Contains("template-que-nao-existe", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Issue #70, item 3 — substituição em passada única (Regex.Replace +
    // MatchEvaluator) no lugar de N chamadas sequenciais de string.Replace.
    // A implementação antiga reprocessava o texto já substituído: se o valor de
    // uma chave contivesse literalmente a sintaxe "{{OutraChave}}", a iteração
    // seguinte do laço trocaria esse trecho pelo valor da outra chave (dupla
    // substituição — injeção de placeholder via dado de usuário).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_ValorContendoSintaxeDeOutroPlaceholder_NaoSofreSegundaSubstituicao()
    {
        // Regressão direta do bug: "NomeAluno" carrega o texto "{{NomeOrientador}}".
        // Com string.Replace sequencial (ordem de inserção do dicionário), a passada de
        // "NomeOrientador" encontraria esse trecho já injetado no corpo e o trocaria por
        // "SECRETO". Com passada única, o resultado do MatchEvaluator não é reescaneado.
        var valores = new Dictionary<string, string>
        {
            ["NomeAluno"] = "João {{NomeOrientador}}",
            ["TituloTcc"] = "Titulo",
            ["NomeOrientador"] = "SECRETO"
        };

        var corpo = _renderer.Render("proposta-aprovada", valores);

        Assert.Contains("João {{NomeOrientador}}", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("João SECRETO", corpo, StringComparison.Ordinal);
        // "SECRETO" só pode aparecer uma vez: no lugar do {{NomeOrientador}} real do template.
        Assert.Equal(1, ContarOcorrencias(corpo, "SECRETO"));
    }

    [Fact]
    public void Render_ValorContendoSintaxeDeOutroPlaceholder_IndependeDaOrdemDoDicionario()
    {
        // Mesmo cenário com a ordem de inserção invertida. Na implementação antiga a
        // dupla substituição dependia da ordem (só ocorria se a chave "vítima" fosse
        // processada DEPOIS); a passada única elimina a ordem como variável.
        var valores = new Dictionary<string, string>
        {
            ["NomeOrientador"] = "SECRETO",
            ["TituloTcc"] = "Titulo",
            ["NomeAluno"] = "João {{NomeOrientador}}"
        };

        var corpo = _renderer.Render("proposta-aprovada", valores);

        Assert.Contains("João {{NomeOrientador}}", corpo, StringComparison.Ordinal);
        Assert.Equal(1, ContarOcorrencias(corpo, "SECRETO"));
    }

    [Fact]
    public void Render_ValorContendoSintaxeDeChaveNaoFornecida_PermaneceLiteral()
    {
        // Variante: o placeholder embutido no valor nem sequer existe no dicionário.
        // O corpo final deve preservar o texto literal, sem lançar e sem limpar.
        var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Aluno {{ChaveNaoFornecida}}"
        });

        Assert.Contains("Aluno {{ChaveNaoFornecida}}", corpo, StringComparison.Ordinal);
        // E os placeholders do próprio template sem valor também ficam intactos.
        Assert.Contains("{{TituloTcc}}", corpo, StringComparison.Ordinal);
        Assert.Contains("{{NomeOrientador}}", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ValorNulo_ViraStringVazia()
    {
        // Contrato preservado da implementação anterior (valor ?? string.Empty):
        // a chave é consumida, o marcador some e nada é escrito no lugar.
        var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = null!,
            ["TituloTcc"] = "Titulo",
            ["NomeOrientador"] = "Orientador"
        });

        Assert.DoesNotContain("{{NomeAluno}}", corpo, StringComparison.Ordinal);
        Assert.Contains("<strong></strong>", corpo, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ValorVazio_RemoveOMarcadorSemDeixarResiduo()
    {
        var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = string.Empty,
            ["TituloTcc"] = "Titulo",
            ["NomeOrientador"] = "Orientador"
        });

        Assert.DoesNotContain("{{NomeAluno}}", corpo, StringComparison.Ordinal);
        Assert.Contains("<strong></strong>", corpo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("proposta-aprovada")]
    [InlineData("proposta-rejeitada")]
    [InlineData("banca-agendada")]
    [InlineData("feedback-registrado")]
    [InlineData("aceite-final")]
    [InlineData("resultado-aprovado")]
    [InlineData("resultado-reprovado")]
    [InlineData("rascunho-reenviado")]
    public void Render_TodosOsTemplatesReais_NaoReprocessamValorInjetadoComSintaxeDePlaceholder(string chave)
    {
        // Varredura sobre os templates reais: para cada placeholder do arquivo, injeta um
        // valor que contém a sintaxe de TODOS os outros placeholders daquele template.
        // Se qualquer um deles fosse reprocessado, o marcador injetado desapareceria.
        var template = LerTemplate(chave);
        var chavesDoTemplate = ExtrairPlaceholders(template);
        Assert.NotEmpty(chavesDoTemplate);

        foreach (var alvo in chavesDoTemplate)
        {
            var injecao = string.Concat(chavesDoTemplate
                .Where(c => c != alvo)
                .Select(c => $"[{{{{{c}}}}}]"));

            if (injecao.Length == 0) continue;

            var valores = chavesDoTemplate.ToDictionary(
                c => c,
                c => c == alvo ? $"INJETADO{injecao}" : $"VALOR-{c}");

            var corpo = _renderer.Render(chave, valores);

            Assert.Contains($"INJETADO{injecao}", corpo, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("proposta-aprovada")]
    [InlineData("proposta-rejeitada")]
    [InlineData("banca-agendada")]
    [InlineData("feedback-registrado")]
    [InlineData("aceite-final")]
    [InlineData("resultado-aprovado")]
    [InlineData("resultado-reprovado")]
    [InlineData("rascunho-reenviado")]
    public void Render_TodosOsTemplatesReais_ComTodasAsChaves_NaoDeixamMarcadorResidual(string chave)
    {
        // Comportamento normal preservado: fornecendo todas as chaves do template,
        // nenhum "{{...}}" sobrevive no corpo final.
        var chavesDoTemplate = ExtrairPlaceholders(LerTemplate(chave));
        var valores = chavesDoTemplate.ToDictionary(c => c, c => $"VALOR-{c}");

        var corpo = _renderer.Render(chave, valores);

        Assert.DoesNotMatch(@"\{\{\w+\}\}", corpo);
        Assert.All(chavesDoTemplate, c => Assert.Contains($"VALOR-{c}", corpo, StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Achado de segurança A06-1 (docs/seguranca/2026-08-18-fix-notificacoes-email-hardening.md):
    // placeholder sem entrada no dicionário permanecia literal em silêncio (risco de drift
    // entre template e código, sem nenhum sinal). Agora gera Warning; o fallback (marcador
    // literal) continua o mesmo, verificado nos testes acima com NullLogger.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_PlaceholderNaoInformado_LogaWarningComAChaveEOTemplate()
    {
        var logger = new LoggerDeCaptura<FileEmailTemplateRenderer>();
        var renderer = new FileEmailTemplateRenderer(logger);

        renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Só o nome",
            ["NomeOrientador"] = "Orientador"
        });

        // proposta-aprovada tem 3 placeholders; só "TituloTcc" ficou sem valor.
        var aviso = Assert.Single(logger.Entradas, e => e.Nivel == LogLevel.Warning);
        Assert.Contains("TituloTcc", aviso.Mensagem, StringComparison.Ordinal);
        Assert.Contains("proposta-aprovada", aviso.Mensagem, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_TodosOsPlaceholdersFornecidos_NaoLogaWarning()
    {
        var logger = new LoggerDeCaptura<FileEmailTemplateRenderer>();
        var renderer = new FileEmailTemplateRenderer(logger);

        renderer.Render("proposta-aprovada", new Dictionary<string, string>
        {
            ["NomeAluno"] = "Maria",
            ["TituloTcc"] = "Titulo",
            ["NomeOrientador"] = "Orientador"
        });

        Assert.DoesNotContain(logger.Entradas, e => e.Nivel == LogLevel.Warning);
    }

    private sealed class LoggerDeCaptura<T> : ILogger<T>
    {
        private readonly List<EntradaDeLog> _entradas = [];

        public IReadOnlyList<EntradaDeLog> Entradas
        {
            get { lock (_entradas) { return _entradas.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entradas)
            {
                _entradas.Add(new EntradaDeLog(logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed record EntradaDeLog(LogLevel Nivel, string Mensagem);

    // ── Auxiliares ────────────────────────────────────────────────────

    private static int ContarOcorrencias(string texto, string procurado)
    {
        var total = 0;
        var indice = texto.IndexOf(procurado, StringComparison.Ordinal);

        while (indice >= 0)
        {
            total++;
            indice = texto.IndexOf(procurado, indice + procurado.Length, StringComparison.Ordinal);
        }

        return total;
    }

    /// <summary>
    /// Lê o template diretamente do embedded resource da DLL de TccManager.Api — mesma
    /// fonte que o renderer usa — para descobrir os placeholders sem duplicar a lista
    /// de chaves aqui (que envelheceria a cada mudança nos arquivos .html).
    /// </summary>
    private static string LerTemplate(string chaveTemplate)
    {
        var assembly = typeof(FileEmailTemplateRenderer).Assembly;
        var nome = $"TccManager.Api.Resources.EmailTemplates.{chaveTemplate}.html";

        using var stream = assembly.GetManifestResourceStream(nome)
            ?? throw new InvalidOperationException($"Template não encontrado como embedded resource: {nome}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<string> ExtrairPlaceholders(string template) =>
        System.Text.RegularExpressions.Regex.Matches(template, @"\{\{(\w+)\}\}")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
}
