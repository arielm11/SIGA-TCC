using Microsoft.AspNetCore.Mvc.ModelBinding;
using TccManager.Shared.DTOs;

namespace TccManager.Api.ModelBinding;

public class PaginacaoQueryModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        if (bindingContext == null)
            throw new ArgumentNullException(nameof(bindingContext));

        var pageResult = bindingContext.ValueProvider.GetValue("page");
        var pageSizeResult = bindingContext.ValueProvider.GetValue("pageSize");

        var page = NormalizarPage(pageResult.FirstValue);
        var pageSize = NormalizarPageSize(pageSizeResult.FirstValue);

        var model = new PaginacaoQuery
        {
            Page = page,
            PageSize = pageSize
        };

        bindingContext.Result = ModelBindingResult.Success(model);
        return Task.CompletedTask;
    }

    private static int NormalizarPage(string? valorBruto)
    {
        if (!int.TryParse(valorBruto, out var page) || page < 1)
            return PaginacaoQuery.DefaultPage;

        return page;
    }

    private static int NormalizarPageSize(string? valorBruto)
    {
        if (!int.TryParse(valorBruto, out var pageSize) || pageSize < 1)
            return PaginacaoQuery.DefaultPageSize;

        if (pageSize > PaginacaoQuery.MaxPageSize)
            return PaginacaoQuery.MaxPageSize;

        return pageSize;
    }
}
