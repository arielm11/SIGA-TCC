using System.Net;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Api.Services;
using TccManager.Api.Services.Email;
using TccManager.Shared.Enums;

namespace TccManager.Api.Services.Notifications;

/// <summary>
/// Orquestração de notificações de negócio: resolve destinatários via AppDbContext
/// (scoped, dentro do request), renderiza o template e enfileira o EmailMessage pronto
/// (IEmailQueue). Não conhece MailKit/SMTP. Nunca lança exceção para o chamador — cada
/// método público envolve seu corpo em try/catch + log (contrato de não-lançamento da
/// arquitetura).
/// </summary>
public class TccNotificationService : ITccNotificationService
{
    private readonly AppDbContext _context;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly IEmailQueue _queue;
    private readonly ILogger<TccNotificationService> _logger;

    public TccNotificationService(
        AppDbContext context,
        IEmailTemplateRenderer renderer,
        IEmailQueue queue,
        ILogger<TccNotificationService> logger)
    {
        _context = context;
        _renderer = renderer;
        _queue = queue;
        _logger = logger;
    }

    public async Task NotificarPropostaAprovadaAsync(int tccId)
    {
        try
        {
            var tcc = await _context.Tccs
                .Include(t => t.Aluno)
                .Include(t => t.Orientador)
                .FirstOrDefaultAsync(t => t.Id == tccId);

            if (tcc == null)
            {
                _logger.LogWarning("NotificarPropostaAprovadaAsync: Tcc {TccId} não encontrado.", tccId);
                return;
            }

            var destinatarios = ColetarEmails(("Aluno", tcc.Aluno?.Email));
            if (destinatarios.Count == 0) return;

            var corpo = _renderer.Render("proposta-aprovada", new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(tcc.Titulo),
                ["NomeOrientador"] = Codificar(tcc.Orientador?.Nome)
            });

            Enfileirar(destinatarios, "Proposta de TCC aprovada", corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de proposta aprovada para o Tcc {TccId}.", tccId);
        }
    }

    public async Task NotificarPropostaRejeitadaAsync(int tccId)
    {
        try
        {
            var tcc = await _context.Tccs
                .Include(t => t.Aluno)
                .FirstOrDefaultAsync(t => t.Id == tccId);

            if (tcc == null)
            {
                _logger.LogWarning("NotificarPropostaRejeitadaAsync: Tcc {TccId} não encontrado.", tccId);
                return;
            }

            var destinatarios = ColetarEmails(("Aluno", tcc.Aluno?.Email));
            if (destinatarios.Count == 0) return;

            var corpo = _renderer.Render("proposta-rejeitada", new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(tcc.Titulo),
                ["MotivoRejeicao"] = Codificar(tcc.MotivoRejeicao) is { Length: > 0 } motivo ? motivo : "Não informado"
            });

            Enfileirar(destinatarios, "Proposta de TCC rejeitada", corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de proposta rejeitada para o Tcc {TccId}.", tccId);
        }
    }

    public async Task NotificarBancaAgendadaAsync(int bancaId)
    {
        try
        {
            var banca = await _context.Banca
                .Include(b => b.Tcc!).ThenInclude(t => t.Aluno)
                .Include(b => b.Tcc!).ThenInclude(t => t.Orientador)
                .Include(b => b.Avaliadores).ThenInclude(a => a.Professor)
                .Include(b => b.Avaliadores).ThenInclude(a => a.MembroExterno)
                .FirstOrDefaultAsync(b => b.Id == bancaId);

            if (banca?.Tcc == null)
            {
                _logger.LogWarning("NotificarBancaAgendadaAsync: Banca {BancaId} ou Tcc associado não encontrado.", bancaId);
                return;
            }

            var candidatos = new List<(string Papel, string? Email)>
            {
                ("Aluno", banca.Tcc.Aluno?.Email),
                ("Orientador", banca.Tcc.Orientador?.Email)
            };

            var nomesAvaliadores = new List<string>();

            foreach (var avaliador in banca.Avaliadores)
            {
                if (avaliador.ProfessorId.HasValue && avaliador.Professor != null)
                {
                    candidatos.Add(($"Avaliador interno {avaliador.Professor.Nome}", avaliador.Professor.Email));
                    nomesAvaliadores.Add(avaliador.Professor.Nome);
                }
                else if (avaliador.MembroExternoId.HasValue && avaliador.MembroExterno != null)
                {
                    candidatos.Add(($"Avaliador externo {avaliador.MembroExterno.Nome}", avaliador.MembroExterno.Email));
                    nomesAvaliadores.Add($"{avaliador.MembroExterno.Nome} ({avaliador.MembroExterno.Instituicao})");
                }
                else
                {
                    // Defesa em profundidade: BancaAvaliador sem ProfessorId nem MembroExternoId
                    // resolvido não deve derrubar a notificação dos demais destinatários (ver
                    // docs/dados — não há constraint XOR no banco garantindo exatamente um preenchido).
                    _logger.LogWarning(
                        "BancaAvaliador {Id} sem Professor nem MembroExterno resolvido; ignorado na notificação da Banca {BancaId}.",
                        avaliador.Id, bancaId);
                }
            }

            var destinatarios = ColetarEmails(candidatos.ToArray());
            if (destinatarios.Count == 0) return;

            var dataHoraBrasilia = BrasiliaTimeZoneService.ConverterDeUtcParaBrasilia(banca.DataHora);
            var listaMembrosHtml = nomesAvaliadores.Count > 0
                ? string.Concat(nomesAvaliadores.Select(n => $"<li>{WebUtility.HtmlEncode(n)}</li>"))
                : "<li>Nenhum avaliador registrado</li>";

            var corpo = _renderer.Render("banca-agendada", new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(banca.Tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(banca.Tcc.Titulo),
                ["DataHora"] = dataHoraBrasilia.ToString("dd/MM/yyyy HH:mm"),
                ["Local"] = Codificar(banca.Local),
                ["ListaMembrosBanca"] = listaMembrosHtml
            });

            Enfileirar(destinatarios, "Banca de TCC agendada", corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de banca agendada para a Banca {BancaId}.", bancaId);
        }
    }

    public async Task NotificarFeedbackRegistradoAsync(int entregaId)
    {
        try
        {
            var entrega = await _context.Entregas
                .Include(e => e.Tcc!).ThenInclude(t => t.Aluno)
                .FirstOrDefaultAsync(e => e.Id == entregaId);

            if (entrega?.Tcc == null)
            {
                _logger.LogWarning("NotificarFeedbackRegistradoAsync: Entrega {EntregaId} ou Tcc associado não encontrado.", entregaId);
                return;
            }

            var destinatarios = ColetarEmails(("Aluno", entrega.Tcc.Aluno?.Email));
            if (destinatarios.Count == 0) return;

            var corpo = _renderer.Render("feedback-registrado", new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(entrega.Tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(entrega.Tcc.Titulo),
                ["TituloEntrega"] = Codificar(entrega.Titulo),
                ["Feedback"] = Codificar(entrega.Feedback) is { Length: > 0 } feedback ? feedback : "Sem comentários adicionais.",
                ["Nota"] = entrega.Nota.HasValue ? entrega.Nota.Value.ToString("0.0") : "Não informada"
            });

            Enfileirar(destinatarios, "Feedback registrado na sua entrega", corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de feedback registrado para a Entrega {EntregaId}.", entregaId);
        }
    }

    public async Task NotificarAceiteFinalAsync(int tccId)
    {
        try
        {
            var tcc = await _context.Tccs
                .Include(t => t.Aluno)
                .FirstOrDefaultAsync(t => t.Id == tccId);

            if (tcc == null)
            {
                _logger.LogWarning("NotificarAceiteFinalAsync: Tcc {TccId} não encontrado.", tccId);
                return;
            }

            var candidatos = new List<(string Papel, string? Email)> { ("Aluno", tcc.Aluno?.Email) };
            candidatos.AddRange(await ObterCandidatosCoordenadoresAsync());

            var destinatarios = ColetarEmails(candidatos.ToArray());
            if (destinatarios.Count == 0) return;

            var corpo = _renderer.Render("aceite-final", new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(tcc.Titulo)
            });

            Enfileirar(destinatarios, "Aceite final concedido", corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de aceite final para o Tcc {TccId}.", tccId);
        }
    }

    public async Task NotificarResultadoBancaAsync(int bancaId, bool aprovado)
    {
        try
        {
            var banca = await _context.Banca
                .Include(b => b.Tcc!).ThenInclude(t => t.Aluno)
                .Include(b => b.Tcc!).ThenInclude(t => t.Orientador)
                .FirstOrDefaultAsync(b => b.Id == bancaId);

            if (banca?.Tcc == null)
            {
                _logger.LogWarning("NotificarResultadoBancaAsync: Banca {BancaId} ou Tcc associado não encontrado.", bancaId);
                return;
            }

            var candidatos = new List<(string Papel, string? Email)>
            {
                ("Aluno", banca.Tcc.Aluno?.Email),
                ("Orientador", banca.Tcc.Orientador?.Email)
            };

            var chaveTemplate = "resultado-aprovado";
            var assunto = "Resultado final da banca: aprovado";

            var valores = new Dictionary<string, string>
            {
                ["NomeAluno"] = Codificar(banca.Tcc.Aluno?.Nome),
                ["TituloTcc"] = Codificar(banca.Tcc.Titulo),
                ["NotaFinal"] = banca.NotaFinal.HasValue ? banca.NotaFinal.Value.ToString("0.0") : "Não informada"
            };

            if (!aprovado)
            {
                candidatos.AddRange(await ObterCandidatosCoordenadoresAsync());
                chaveTemplate = "resultado-reprovado";
                assunto = "Resultado final da banca: reprovado";
                valores["MotivoRejeicao"] = Codificar(banca.Tcc.MotivoRejeicao) is { Length: > 0 } motivo ? motivo : "Não informado";
            }

            var destinatarios = ColetarEmails(candidatos.ToArray());
            if (destinatarios.Count == 0) return;

            var corpo = _renderer.Render(chaveTemplate, valores);

            Enfileirar(destinatarios, assunto, corpo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao preparar notificação de resultado de banca para a Banca {BancaId}.", bancaId);
        }
    }

    private async Task<IEnumerable<(string Papel, string? Email)>> ObterCandidatosCoordenadoresAsync()
    {
        var coordenadores = await _context.Usuarios
            .Where(u => u.Tipo == TipoUsuario.Coordenador && u.Ativo)
            .Select(u => new { u.Nome, u.Email })
            .ToListAsync();

        return coordenadores.Select(c => ($"Coordenador {c.Nome}", (string?)c.Email));
    }

    /// <summary>
    /// Filtra candidatos com e-mail vazio/nulo, logando um aviso individual por
    /// destinatário descartado (Usuario.Email e MembroExterno.Email são NOT NULL no banco,
    /// mas podem ser string vazia via PUT sem validação — ver docs/dados). Nunca lança.
    /// </summary>
    private List<string> ColetarEmails(params (string Papel, string? Email)[] candidatos)
    {
        var validos = new List<string>();

        foreach (var (papel, email) in candidatos)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogWarning("Destinatário '{Papel}' sem e-mail válido; notificação não será enviada para ele.", papel);
                continue;
            }

            validos.Add(email);
        }

        return validos.Distinct().ToList();
    }

    /// <summary>
    /// Enfileira uma EmailMessage por destinatário (nunca todos no mesmo To:), para que
    /// destinatários de uma mesma notificação (ex.: aluno, avaliadores externos, coordenadores)
    /// não vejam o endereço de e-mail uns dos outros, e para que um endereço malformado
    /// não descarte o envio para os demais.
    /// </summary>
    private void Enfileirar(IReadOnlyList<string> destinatarios, string assunto, string corpoHtml)
    {
        if (destinatarios.Count == 0)
        {
            _logger.LogWarning("Notificação '{Assunto}' sem destinatários válidos; e-mail não enviado.", assunto);
            return;
        }

        foreach (var destinatario in destinatarios)
        {
            _queue.Enqueue(new EmailMessage(new[] { destinatario }, assunto, corpoHtml));
        }
    }

    private static string Codificar(string? valor) => WebUtility.HtmlEncode(valor ?? string.Empty);
}
