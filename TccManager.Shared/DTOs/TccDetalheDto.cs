using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Shared.DTOs;

// Issue #144 (achados A3/B7/B8 e M6 da auditoria de 2026-09-30): substitui a entidade Tcc
// crua antes devolvida por GetMeuTcc/GetDetalhesTcc/SubmeterProposta — mesma classe de risco
// do vazamento de Usuario.SenhaHash corrigido na #137 (qualquer campo novo em Tcc/Usuario/
// Entrega sem [JsonIgnore] vazava automaticamente por esses endpoints). Não inclui
// ArquivoCaminho (nome original do arquivo em disco, possível PII) nem navegações completas
// de Usuario — só o que cada tela consumidora de fato usa.
public class TccDetalheDto
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Resumo { get; set; } = string.Empty;
    public string? MotivoRejeicao { get; set; }
    public DateTime DataCriacao { get; set; }
    public StatusTcc Status { get; set; }

    public int AlunoId { get; set; }

    // Preenchido só por GetDetalhesTcc (visão do Professor) — GetMeuTcc/SubmeterProposta
    // deixam null (o próprio Aluno não precisa do próprio nome).
    public string? NomeAluno { get; set; }

    public int? OrientadorId { get; set; }
    public int? OrientadorSolicitadoId { get; set; }
    public string? NomeOrientadorSolicitado { get; set; }

    public List<EntregaDto> Entregas { get; set; } = new();
    public List<AcompanhamentoResumoDto> Acompanhamentos { get; set; } = new();

    public static TccDetalheDto DeEntidade(Tcc tcc, string? nomeAluno = null) => new()
    {
        Id = tcc.Id,
        Titulo = tcc.Titulo,
        Resumo = tcc.Resumo,
        MotivoRejeicao = tcc.MotivoRejeicao,
        DataCriacao = tcc.DataCriacao,
        Status = tcc.Status,
        AlunoId = tcc.AlunoId,
        NomeAluno = nomeAluno,
        OrientadorId = tcc.OrientadorId,
        OrientadorSolicitadoId = tcc.OrientadorSolicitadoId,
        NomeOrientadorSolicitado = tcc.NomeOrientadorSolicitado,
        Entregas = tcc.Entregas.Select(EntregaDto.DeEntidade).ToList(),
        Acompanhamentos = tcc.Acompanhamentos.Select(AcompanhamentoResumoDto.DeEntidade).ToList()
    };
}
