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
