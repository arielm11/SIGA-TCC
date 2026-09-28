using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using TccManager.Shared.Enums;

namespace TccManager.Shared.Models;
public class Tcc
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Titulo { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Resumo { get; set; } = string.Empty;

    public string? ArquivoCaminho { get; set; }

    [MaxLength(2000)]
    public string? MotivoRejeicao { get; set; }

    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public StatusTcc Status { get; set; } = StatusTcc.Pendente;
    
    public int AlunoId { get; set; }
    [ForeignKey("AlunoId")]
    public Usuario? Aluno { get; set; }

    public int? OrientadorId { get; set; }
    [ForeignKey("OrientadorId")]
    public Usuario? Orientador { get; set; }

    public ICollection<Entrega> Entregas { get; set; } = new List<Entrega>();
    public ICollection<Acompanhamento> Acompanhamentos { get; set; } = new List<Acompanhamento>();

    // Issue #113 (achado A06-1): token de concorrência otimista (SQL Server rowversion,
    // atualizado pelo próprio banco a cada UPDATE). Sem isso, DesignarOrientador/
    // RejeitarProposta faziam read-then-write só com a guarda "Status == Pendente" avaliada
    // no read — dois Coordenadores agindo ao mesmo tempo sobre a mesma proposta podiam ambos
    // passar e ambos salvar (ex.: Reprovado com OrientadorId preenchido). Com [Timestamp], o
    // EF Core inclui RowVersion no WHERE do UPDATE e lança DbUpdateConcurrencyException
    // (tratada como 409) se a linha já mudou desde a leitura.
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
