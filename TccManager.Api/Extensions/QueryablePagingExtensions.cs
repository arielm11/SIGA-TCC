using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Extensions;

public static class QueryablePagingExtensions
{
    /// <summary>
    /// Issue #74: <c>cancellationToken</c> propagado para <c>CountAsync</c>/<c>ToListAsync</c>
    /// — sem isso, uma requisição de listagem paginada cancelada pelo cliente (aba fechada,
    /// navegação, timeout) continuava executando as duas queries no servidor até o fim, sem
    /// nenhum benefício (a resposta nunca seria entregue).
    ///
    /// Issue #107 (achado F-06): o parâmetro era opcional (<c>default</c>) para não quebrar
    /// chamadores existentes, mas isso deixava a proteção reversível em silêncio — um futuro
    /// endpoint paginado que esquecesse o argumento voltaria ao comportamento pré-#74 sem
    /// nenhum aviso do compilador. Tornado obrigatório: os 4 call sites de produção já
    /// propagavam <c>HttpContext.RequestAborted</c> corretamente.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, PaginacaoQuery paginacao, CancellationToken cancellationToken)
    {
        var pageSize = paginacao.PageSize <= 0 ? 1 : paginacao.PageSize;
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        // Clampa a página ao intervalo real [1, totalPages] em vez de usar
        // paginacao.Page diretamente: evita overflow de Int32 em (Page-1)*PageSize
        // para um Page muito grande, e evita OFFSET profundo desnecessário no banco.
        var page = totalPages > 0 ? Math.Min(Math.Max(paginacao.Page, 1), totalPages) : 1;
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>
        {
            Items = items,
            TotalCount = totalCount,
            TotalPages = totalPages,
            CurrentPage = page,
            PageSize = pageSize
        };
    }
}
