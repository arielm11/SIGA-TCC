using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Extensions;

public static class QueryablePagingExtensions
{
    /// <summary>
    /// Issue #74: <c>cancellationToken</c> propagado para <c>CountAsync</c>/<c>ToListAsync</c>
    /// — sem isso, uma requisição de listagem paginada cancelada pelo cliente (aba fechada,
    /// navegação, timeout) continuava executando as duas queries no servidor até o fim, sem
    /// nenhum benefício (a resposta nunca seria entregue). O parâmetro é opcional
    /// (<c>default</c>) para não quebrar nenhum chamador existente que ainda não propague um
    /// token — mas todo controller desta base já tem acesso a
    /// <c>HttpContext.RequestAborted</c> e deveria passá-lo.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, PaginacaoQuery paginacao, CancellationToken cancellationToken = default)
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
