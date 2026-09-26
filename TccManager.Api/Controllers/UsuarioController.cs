using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Security.Claims;
using TccManager.Api.Configuration;
using TccManager.Api.Data;
using TccManager.Shared.DTOs;
using TccManager.Shared.Models;
using TccManager.Shared.Enums;
using TccManager.Shared.Validation;

namespace TccManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsuarioController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<UsuarioController> _logger;

    public UsuarioController(AppDbContext context, ILogger<UsuarioController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUsuarios()
    {
        var usuarios = await _context.Usuarios
            .Select(u => new UsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                Tipo = u.Tipo,
                Ativo = u.Ativo
            })
            .ToListAsync();

        return Ok(usuarios);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMeuPerfil()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("nameid")?.Value;

        if (string.IsNullOrEmpty(userIdClaim))
            return Unauthorized();

        var usuario = await _context.Usuarios.FindAsync(int.Parse(userIdClaim));

        if (usuario == null)
            return NotFound("Usuário não encontrado.");

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Tipo = usuario.Tipo,
            Ativo = usuario.Ativo
        };

        return Ok(usuarioDto);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUsuarioById(int id)
    {
        if (!PodeAcessarOuEditar(id))
        {
            _logger.LogWarning(
                "Acesso negado em GET /api/usuario/{{id}}. Solicitante: {SolicitanteId}, alvo: {AlvoId}",
                ObterIdClaimAutenticado() ?? "desconhecido",
                id);
            return Forbid();
        }

        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        var usuarioDto = new UsuarioDto
        {
            Id = usuario.Id,
            Nome = usuario.Nome,
            Email = usuario.Email,
            Tipo = usuario.Tipo,
            Ativo = usuario.Ativo
        };

        return Ok(usuarioDto);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateUsuario([FromBody] UsuarioDto dto)
    {
        if(await _context.Usuarios.AnyAsync(u => u.Email == dto.Email))
            return BadRequest("Email já cadastrado");

        // Issue #90 (achado A07-1): sem esta checagem, POST sem o campo Senha (ou com uma
        // senha trivial) cria um usuário com senha vazia/fraca funcional
        // (BCrypt.Verify("", hash) == true, verificado empiricamente). Mesma política já
        // usada no bootstrap de Admin e na troca de senha obrigatória (issue #88) —
        // reaproveitada aqui, não uma nova decisão de produto.
        if (!PoliticaSenha.Valida(dto.Senha, dto.Email, out var motivoSenhaCriacao))
            return BadRequest(motivoSenhaCriacao);

        string passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

        var newUsuario = new Usuario
        {
            Nome = dto.Nome,
            Email = dto.Email,
            SenhaHash = passwordHash,
            Tipo = dto.Tipo,
            Ativo = true
        };

        _context.Usuarios.Add(newUsuario);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Não logar a exceção crua nem o email: o índice único de email pode
            // ser violado numa corrida com o pre-check acima, e a exceção do
            // provedor relacional traz o valor duplicado na mensagem (LGPD).
            // O "when" restringe o catch à violação de unicidade (2601/2627) —
            // outras causas de DbUpdateException (banco indisponível, outra
            // constraint) propagam em vez de virar um falso "email já em uso".
            _logger.LogWarning("Falha ao salvar usuário em POST /api/usuario: possível violação de restrição de unicidade de email.");
            return Conflict("Não foi possível salvar o usuário. Verifique se o email já está em uso.");
        }

        // Issue #90 (achado A09-2): criação de usuário (inclusive de um Admin) não tinha
        // nenhum registro de auditoria — paridade com os demais eventos sensíveis deste
        // controller. Nunca loga e-mail (LGPD), só o id gerado e o tipo.
        _logger.LogWarning(
            "Usuário criado com sucesso via POST /api/usuario. Admin: {AdminId}, novo usuário: {NovoUsuarioId}, Tipo: {Tipo}",
            ObterIdClaimAutenticado() ?? "desconhecido",
            newUsuario.Id,
            newUsuario.Tipo);

        var novoUsuarioDto = new UsuarioDto
        {
            Id = newUsuario.Id,
            Nome = newUsuario.Nome,
            Email = newUsuario.Email,
            Tipo = newUsuario.Tipo,
            Ativo = newUsuario.Ativo
        };

        return Ok(novoUsuarioDto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUsuario(int id, [FromBody] UsuarioDto dto)
    {
        if (!PodeAcessarOuEditar(id))
        {
            _logger.LogWarning(
                "Acesso negado em PUT /api/usuario/{{id}}. Solicitante: {SolicitanteId}, alvo: {AlvoId}",
                ObterIdClaimAutenticado() ?? "desconhecido",
                id);
            return Forbid();
        }

        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        if (dto.Email != usuario.Email &&
            await _context.Usuarios.AnyAsync(u => u.Email == dto.Email && u.Id != id))
        {
            return BadRequest("Email já cadastrado");
        }

        // Issue #90 (achado A07-1): mesma política do POST — reaproveitada, não decidida
        // agora. Só se aplica quando uma nova senha é de fato enviada (Senha vazia
        // continua significando "manter a atual", comportamento já existente).
        if (!string.IsNullOrEmpty(dto.Senha) && !PoliticaSenha.Valida(dto.Senha, dto.Email, out var motivoSenhaEdicao))
            return BadRequest(motivoSenhaEdicao);

        usuario.Nome = dto.Nome;
        usuario.Email = dto.Email;

        // Issue #90 (achado A06-2, TOCTOU): a checagem "sou o único Admin ativo?" e a
        // escrita que a viola precisam estar na MESMA transação Serializable — sem isso,
        // duas requisições concorrentes (ex.: dois PUT desativando dois Admins distintos
        // enquanto só existem 2 ativos) podem ambas ler "> 1 Admin ativo", passar, e
        // deixar o sistema com ZERO Admins ativos (estado sem recuperação pela API, ver
        // #88). O provider InMemory usado pelos testes não suporta transações (mesma
        // limitação conhecida do #91 para constraints relacionais) — a proteção real só
        // existe contra um banco relacional.
        var transacional = _context.Database.IsRelational();
        var transacao = transacional
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;

        try
        {
            var bloqueadoPorUnicoAdmin = false;
            string? mensagemBloqueio = null;

            // Tipo e Ativo sao campos sensiveis (escalacao de privilegio) e so podem
            // ser alterados por um Admin. Em autoedicao, o valor enviado pelo cliente
            // e ignorado e o valor atual do servidor e preservado.
            if (User.IsInRole("Admin"))
            {
                var tipoAntigo = usuario.Tipo;
                var ativoAntigo = usuario.Ativo;

                var eraUnicoAdminAtivo = tipoAntigo == TipoUsuario.Admin && ativoAntigo
                    && (dto.Tipo != TipoUsuario.Admin || !dto.Ativo)
                    && await EhUnicoAdminAtivoAsync();

                if (eraUnicoAdminAtivo)
                {
                    bloqueadoPorUnicoAdmin = true;
                    mensagemBloqueio = "Não é possível remover o papel de Admin ou desativar o único Admin ativo do sistema.";

                    _logger.LogWarning(
                        "Alteração de campos sensíveis bloqueada em PUT /api/usuario/{{id}}: alvo é o único Admin ativo do sistema. Admin solicitante: {AdminId}, alvo: {AlvoId}, Tipo solicitado: {TipoSolicitado}, Ativo solicitado: {AtivoSolicitado}",
                        ObterIdClaimAutenticado() ?? "desconhecido",
                        id,
                        dto.Tipo,
                        dto.Ativo);
                }
                else
                {
                    usuario.Tipo = dto.Tipo;
                    usuario.Ativo = dto.Ativo;

                    if (tipoAntigo != usuario.Tipo || ativoAntigo != usuario.Ativo)
                    {
                        _logger.LogWarning(
                            "Alteração de campos sensíveis em PUT /api/usuario/{{id}}. Admin: {AdminId}, alvo: {AlvoId}, Tipo: {TipoAntigo} -> {TipoNovo}, Ativo: {AtivoAntigo} -> {AtivoNovo}",
                            ObterIdClaimAutenticado() ?? "desconhecido",
                            id,
                            tipoAntigo,
                            usuario.Tipo,
                            ativoAntigo,
                            usuario.Ativo);
                    }
                }
            }

            if (!string.IsNullOrEmpty(dto.Senha))
            {
                usuario.SenhaHash = BCrypt.Net.BCrypt.HashPassword(dto.Senha);

                // Issue #88 (D7): defensivo — no MVP este ramo é inalcançável para um usuário
                // com a flag ligada (ele nunca obtém token para chegar ao PUT, D8), mas evita um
                // estado preso caso a flag venha a ser usada em outro fluxo no futuro.
                usuario.PrecisaTrocarSenha = false;
            }

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                // Não logar a exceção crua nem o email: o índice único de email pode
                // ser violado numa corrida com o pre-check acima, e a exceção do
                // provedor relacional traz o valor duplicado na mensagem (LGPD).
                // O "when" restringe o catch à violação de unicidade (2601/2627) —
                // outras causas de DbUpdateException (banco indisponível, outra
                // constraint) propagam em vez de virar um falso "email já em uso".
                _logger.LogWarning(
                    "Falha ao salvar usuário em PUT /api/usuario/{{id}}: possível violação de restrição de unicidade de email. Alvo: {AlvoId}",
                    id);
                return Conflict("Não foi possível salvar o usuário. Verifique se o email já está em uso.");
            }

            if (transacao != null)
                await transacao.CommitAsync();

            // Issue #89: o bloqueio do último Admin agora reporta 409 (não mais 200
            // silencioso) — os demais campos da requisição (Nome/Email/Senha) já foram
            // salvos normalmente, só Tipo/Ativo foram preservados.
            if (bloqueadoPorUnicoAdmin)
                return Conflict(mensagemBloqueio);

            var usuarioDto = new UsuarioDto
            {
                Id = usuario.Id,
                Nome = usuario.Nome,
                Email = usuario.Email,
                Tipo = usuario.Tipo,
                Ativo = usuario.Ativo
            };

            return Ok(usuarioDto);
        }
        finally
        {
            if (transacao != null)
                await transacao.DisposeAsync();
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteUsuario(int id)
    {
        var usuario = await _context.Usuarios.FindAsync(id);

        if (usuario == null)
            return NotFound("Usuário não encontrado");

        // Issue #90 (achado A06-2, TOCTOU): mesma proteção do PUT — a checagem do
        // último Admin e a exclusão precisam estar na mesma transação Serializable,
        // senão dois DELETE (ou um DELETE + um PUT) concorrentes podem ambos ler
        // "> 1 Admin ativo" e deixar o sistema sem nenhum. InMemory (testes) não
        // suporta transação — proteção real só existe contra banco relacional.
        var transacional = _context.Database.IsRelational();
        var transacao = transacional
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;

        try
        {
            if (usuario.Tipo == TipoUsuario.Admin && usuario.Ativo && await EhUnicoAdminAtivoAsync())
            {
                // Issue #90 (achado A09-2): recusa de DELETE não tinha nenhum registro de
                // auditoria — paridade com a recusa equivalente do PUT, acima.
                _logger.LogWarning(
                    "Exclusão recusada em DELETE /api/usuario/{{id}}: alvo é o único Admin ativo do sistema. Admin solicitante: {AdminId}, alvo: {AlvoId}",
                    ObterIdClaimAutenticado() ?? "desconhecido",
                    id);
                return BadRequest("Não é possível excluir o único Admin ativo do sistema.");
            }

            // Professor orientando TCC ou compondo banca tem FK NO ACTION/Restrict para
            // usuarios: excluir sem checar antes derruba em DbUpdateException (500).
            var possuiVinculos = await _context.Tccs.AnyAsync(t => t.OrientadorId == id)
                || await _context.BancaAvaliadores.AnyAsync(b => b.ProfessorId == id);

            if (possuiVinculos)
                return Conflict("Não é possível excluir: o usuário orienta TCC(s) e/ou participa de banca(s) avaliadora(s).");

            _context.Usuarios.Remove(usuario);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx)
            {
                // Issue #90 (achado do qa-agent, Ressalva 4): este catch é um backstop
                // para violação de integridade referencial não coberta pelas checagens
                // acima (ex.: vínculo futuro ainda não previsto aqui) — mesmo padrão de
                // Create/UpdateUsuario, agora com o "when" e o SqlErrorNumber logado em
                // vez de um catch genérico e cru (nunca ex.Message: pode trazer dados).
                _logger.LogWarning(
                    "Falha ao excluir usuário em DELETE /api/usuario/{{id}}: possível violação de integridade referencial (SqlErrorNumber {SqlErrorNumber}). Alvo: {AlvoId}",
                    sqlEx.Number,
                    id);
                return Conflict("Não é possível excluir este usuário: existem registros vinculados a ele.");
            }

            if (transacao != null)
                await transacao.CommitAsync();

            // Issue #90 (achado A09-2): exclusão bem-sucedida (destrutiva, com cascade)
            // não tinha nenhum registro de auditoria.
            _logger.LogWarning(
                "Usuário excluído com sucesso via DELETE /api/usuario/{{id}}. Admin: {AdminId}, alvo: {AlvoId}",
                ObterIdClaimAutenticado() ?? "desconhecido",
                id);

            return Ok("Usuário deletado com sucesso");
        }
        finally
        {
            if (transacao != null)
                await transacao.DisposeAsync();
        }
    }

    // Issue #74 (achado F-01 da revisão de segurança,
    // docs/seguranca/2026-08-27-paginacao-cancellation-rate-limiting.md): este endpoint
    // devolve o mesmo catálogo de professores que CoordenadorController.GetProfessores (com
    // Email a mais, sem paginação), acessível a QUALQUER papel autenticado — sem a mesma
    // política aplicada aqui, a proteção adicionada ao endpoint do Coordenador seria
    // contornável trivialmente por este caminho equivalente e mais permissivo.
    [HttpGet("professores")]
    [Authorize]
    [EnableRateLimiting(RateLimitingSetup.ListagemPaginadaPolicyName)]
    public async Task<IActionResult> GetProfessores()
    {
        var professores = await _context.Usuarios
            .Where(u => u.Tipo == TipoUsuario.Professor && u.Ativo)
            .Select(u => new UsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email,
                Tipo = u.Tipo,
                Ativo = u.Ativo
            })
            .ToListAsync();

        return Ok(professores);
    }

    // Autoriza a operacao se o usuario autenticado for Admin ou se estiver
    // acessando/editando o proprio registro (id da rota == id do proprio claim).
    private bool PodeAcessarOuEditar(int id)
    {
        if (User.IsInRole("Admin"))
            return true;

        var userIdClaim = ObterIdClaimAutenticado();

        return int.TryParse(userIdClaim, out var usuarioIdAutenticado)
            && usuarioIdAutenticado == id;
    }

    private string? ObterIdClaimAutenticado() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("nameid")?.Value;

    // Verifica se existe apenas 1 Admin ativo no sistema no momento da chamada.
    // Usado para impedir que a API deixe o sistema sem nenhum Admin ativo
    // (auto-degradacao/desativacao ou exclusao do ultimo Admin).
    // Issue #88 (D12): delega a UsuarioQueries.ContarAdminsAtivosAsync, a mesma query usada
    // pelo bootstrap (== 0) — uma única definição de "Admin ativo" no código.
    private async Task<bool> EhUnicoAdminAtivoAsync() =>
        await _context.ContarAdminsAtivosAsync() <= 1;
}