using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TccManager.Api.Data;
using TccManager.Shared.Models;

namespace TccManager.Api.Services.Pdf;

public class RascunhoAtaTokenService : IRascunhoAtaTokenService
{
    private readonly AppDbContext _context;

    public RascunhoAtaTokenService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<string> GerarTokenAsync(int bancaId, int membroExternoId)
    {
        var banca = await _context.Banca
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bancaId);

        if (banca == null)
            throw new InvalidOperationException($"Banca {bancaId} não encontrada ao gerar token de rascunho.");

        // Revoga qualquer token vigente do mesmo par antes de gerar o novo — garante a
        // idempotência do reenvio (RF-06) e, junto com o índice único filtrado do banco,
        // a invariante "no máximo 1 token ativo por par" (ver docs/dados, seção 3.1).
        await RevogarAtivosSemSalvarAsync(bancaId, membroExternoId);

        // CSPRNG (não Guid.NewGuid): o token é uma credencial de portador, precisa de
        // garantia de imprevisibilidade criptográfica, não apenas unicidade — mesmo
        // raciocínio já usado em AuthTokenService para o refresh token.
        var tokenBruto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var agora = DateTime.UtcNow;

        var novoToken = new RascunhoAtaToken
        {
            BancaId = bancaId,
            MembroExternoId = membroExternoId,
            TokenHash = CalcularHash(tokenBruto),
            CreatedAtUtc = agora,
            ExpiresAtUtc = banca.DataHora
        };

        _context.RascunhoAtaTokens.Add(novoToken);
        await _context.SaveChangesAsync();

        return tokenBruto;
    }

    public async Task<RascunhoTokenValidacao> ValidarAsync(string tokenBruto)
    {
        var hash = CalcularHash(tokenBruto);

        var token = await _context.RascunhoAtaTokens
            .Include(t => t.Banca)
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        // Token inexistente ou revogado: resposta genérica (Invalido) para não vazar
        // existência/estado de token a um chamador anônimo.
        if (token?.Banca == null || token.RevokedAtUtc != null)
            return new RascunhoTokenValidacao { Status = RascunhoTokenValidacaoStatus.Invalido };

        // Bloqueio definitivo (RNF-03): checado antes da expiração por data, para que o
        // resultado já registrado sempre prevaleça (410) mesmo quando a data da banca já
        // passou — os 3 pontos de acesso devem convergir no mesmo status nesse cenário.
        if (token.Banca.NotaFinal != null)
            return new RascunhoTokenValidacao { Status = RascunhoTokenValidacaoStatus.ResultadoRegistrado, BancaId = token.BancaId };

        if (DateTime.UtcNow >= token.Banca.DataHora)
            return new RascunhoTokenValidacao { Status = RascunhoTokenValidacaoStatus.Invalido };

        return new RascunhoTokenValidacao { Status = RascunhoTokenValidacaoStatus.Valido, BancaId = token.BancaId };
    }

    public async Task RevogarTokenAtualAsync(int bancaId, int membroExternoId)
    {
        var alterouAlgum = await RevogarAtivosSemSalvarAsync(bancaId, membroExternoId);

        if (alterouAlgum)
            await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Carrega os tokens ativos do par via change tracker (não <c>ExecuteUpdateAsync</c> —
    /// o provider EF Core InMemory da suíte de testes não o suporta, mesmo motivo já
    /// documentado em <c>AuthTokenService.RevokeAllForUserAsync</c>) e marca
    /// <c>RevokedAtUtc</c>. Não salva: quem chama decide o momento do <c>SaveChangesAsync</c>.
    /// </summary>
    private async Task<bool> RevogarAtivosSemSalvarAsync(int bancaId, int membroExternoId)
    {
        var tokensAtivos = await _context.RascunhoAtaTokens
            .Where(t => t.BancaId == bancaId && t.MembroExternoId == membroExternoId && t.RevokedAtUtc == null)
            .ToListAsync();

        if (tokensAtivos.Count == 0)
            return false;

        var agora = DateTime.UtcNow;
        foreach (var token in tokensAtivos)
        {
            token.RevokedAtUtc = agora;
        }

        return true;
    }

    private static string CalcularHash(string valor)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(valor));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
