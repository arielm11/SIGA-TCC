using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace TccManager.Shared.Models;

[Table("usuarios")]
public class Usuario : BaseModel
{
    [PrimaryKey("id", false)]
    public int Id { get; set; }

    [Column("Nome")]
    public string Nome { get; set; } = string.Empty;

    [Column("Email")]
    public string Email { get; set; } = string.Empty;

    [Column("SenhaHash")]
    public string SenhaHash { get; set; } = string.Empty;

    [Column("Admin")]
    public bool Admin { get; set; }

    [Column("Ativo")]
    public bool Ativo { get; set; }
}