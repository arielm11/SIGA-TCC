using Microsoft.AspNetCore.Mvc.ModelBinding;
using TccManager.Shared.DTOs;

namespace TccManager.Api.ModelBinding;

public class PaginacaoQueryModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        if (context.Metadata.ModelType == typeof(PaginacaoQuery))
            return new PaginacaoQueryModelBinder();

        return null;
    }
}
