using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using TccManager.Shared.Enums;

namespace TccManager.Shared.Models;

[Table("usuarios")]
public class Usuario
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Nome { get; set; } = string.Empty;

    [MaxLength(450)]
    public string Email { get; set; } = string.Empty;

    // Achado de segurança (2026-09-28, descoberto durante a arquitetura do #112): controllers
    // que devolvem a entidade Usuario/Tcc crua (ex.: OrientadorController.GetDetalhesTcc, que
    // faz .Include(t => t.Aluno) e retorna Ok(tcc)) serializavam este campo sem nenhuma
    // proteção — qualquer requisição autorizada a ver o TCC recebia o hash bcrypt da senha do
    // Aluno/Orientador junto. [JsonIgnore] é a defesa em profundidade: mesmo que um endpoint
    // futuro (ou um dos já existentes) volte a serializar a entidade inteira em vez de um DTO
    // projetado, o hash nunca sai do servidor.
    [JsonIgnore]
    public string SenhaHash { get; set; } = string.Empty;

    public TipoUsuario Tipo { get; set; } = TipoUsuario.Aluno;

    public bool Ativo { get; set; }

    public int LimiteOrientandos { get; set; } = 5;
    public bool AceitandoOrientandos { get; set; } = true;

    // Issue #88 (RF-04): liga apenas pelo bootstrap de Admin (AdminBootstrapSetup);
    // desliga apenas pelo endpoint de troca forçada (AuthController) e, defensivamente,
    // pela autoedição de senha em UsuarioController.UpdateUsuario. Fora de UsuarioDto de
    // propósito — ver docs/arquitetura/2026-09-03-bootstrap-primeiro-admin.md, D7.
    public bool PrecisaTrocarSenha { get; set; } = false;
}