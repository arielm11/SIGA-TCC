namespace TccManager.Shared.DTOs;

public class DashboardCoordenadorDto
{
    public int TotalAtivos { get; set; }
    public int AguardandoBanca { get; set; }
    public int PropostasPendentes { get; set; }
    public int TccsConcluidos { get; set; }
}

public class ProfessorResumoDto 
{ 
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public int CargaAtual { get; set; }
    public int LimiteOrientandos { get; set; }
    public bool AceitandoOrientandos { get; set; }
}

public class DesignarOrientadorDto
{
    public int OrientadorId { get; set; }
}

public class CapacidadeProfessorDto
{
    public int LimiteOrientandos { get; set; }
    public bool AceitandoOrientandos { get; set; }
}

public class TccAguardandoBancaDto
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;
    public string NomeOrientador { get; set; } = string.Empty;
}

public class BancaPendenteDto
{
    public int TccId { get; set; }
    public DateTime DataHora { get; set; }
    public string Local { get; set; } = string.Empty;
    public string TccTitulo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;

    /// <summary>
    /// Membros externos avaliadores desta banca (N2 Etapa 2) — usado pelo Client para
    /// renderizar o botão de reenvio de token do rascunho (RF-06) individualmente por
    /// membro. Vazio se a banca só tiver avaliadores internos.
    /// </summary>
    public List<MembroExternoBancaDto> MembrosExternos { get; set; } = new();
}

/// <summary>Membro externo avaliador de uma banca, usado por BancaPendenteDto (RF-06/Etapa 2).</summary>
public class MembroExternoBancaDto
{
    public int MembroExternoId { get; set; }
    public string Nome { get; set; } = string.Empty;
}

/// <summary>
/// Item da listagem de bancas já concluídas (resultado registrado). Usa "BancaId"
/// (e não "TccId", como o já existente BancaPendenteDto faz de forma enganosa) para
/// que o Client monte a rota banca/{BancaId}/ata-pdf sem ambiguidade.
/// </summary>
public class BancaConcluidaDto
{
    public int BancaId { get; set; }
    public string TccTitulo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;
    public DateTime DataHora { get; set; }
    public decimal NotaFinal { get; set; }
    public bool Aprovado { get; set; }
}