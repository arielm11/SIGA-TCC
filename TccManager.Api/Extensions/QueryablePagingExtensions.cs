using Microsoft.EntityFrameworkCore;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Extensions;

public static class QueryablePagingExtensions
{
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(this IQueryable<T> query, PaginacaoQuery paginacao)
    {
        var pageSize = paginacao.PageSize <= 0 ? 1 : paginacao.PageSize;
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        // Clampa a página ao intervalo real [1, totalPages] em vez de usar
        // paginacao.Page diretamente: evita overflow de Int32 em (Page-1)*PageSize
        // para um Page muito grande, e evita OFFSET profundo desnecessário no banco.
        var page = totalPages > 0 ? Math.Min(Math.Max(paginacao.Page, 1), totalPages) : 1;
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

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
