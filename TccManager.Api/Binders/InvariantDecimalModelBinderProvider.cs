using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TccManager.Api.Binders;

public class InvariantDecimalModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        var modelType = context.Metadata.ModelType;

        if (modelType == typeof(decimal) || modelType == typeof(decimal?))
            return new InvariantDecimalModelBinder();


        return null;
    }
}