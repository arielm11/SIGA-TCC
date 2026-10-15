using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
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

    // Issue #112: professor pedido pelo Aluno na submissão da proposta (RF01) — registro
    // histórico do pedido, nunca reescrito depois (nem pela decisão do próprio professor, nem
    // por DesignarOrientador do Coordenador). O vínculo operacional continua sendo
    // OrientadorId, preenchido só após decisão positiva. Opcional (P1 do documento de produto).
    public int? OrientadorSolicitadoId { get; set; }
    [ForeignKey("OrientadorSolicitadoId")]
    // [JsonIgnore] (defesa em profundidade, mesmo padrão de Usuario.SenhaHash em #137): os
    // controllers desta feature nunca populam esta navegação via entidade rastreada (usam
    // AnyAsync/projeção), mas GetMeuTcc/GetDetalhesTcc devolvem a entidade Tcc crua — se o
    // EF Core algum dia fizer relationship fix-up aqui, a navegação nunca deve ser serializada.
    [JsonIgnore]
    public Usuario? OrientadorSolicitado { get; set; }

    // Issue #112 (D7/RF04): transporte do nome do professor solicitado, preenchido só por
    // GetMeuTcc via consulta projetada (nunca via a navegação OrientadorSolicitado, que está
    // sempre null nos fluxos desta feature — ver D6). Não mapeado: puramente de transporte.
    [NotMapped]
    public string? NomeOrientadorSolicitado { get; set; }

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
