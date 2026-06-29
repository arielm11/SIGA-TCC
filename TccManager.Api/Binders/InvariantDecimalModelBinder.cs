using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TccManager.Api.Binders;

public class InvariantDecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        if (bindingContext == null)
            throw new ArgumentNullException(nameof(bindingContext));

        var modelName = bindingContext.ModelName;
        var valueProviderResult = bindingContext.ValueProvider.GetValue(modelName);

        if (valueProviderResult == ValueProviderResult.None)
            // Campo não enviado — deixa o framework decidir se é obrigatório ou não
            return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(modelName, valueProviderResult);

        var valorBruto = valueProviderResult.FirstValue;

        if (string.IsNullOrWhiteSpace(valorBruto))
        {
            // Permite nullable decimal (decimal?) vir vazio sem erro
            if (Nullable.GetUnderlyingType(bindingContext.ModelType) != null)
            {
                bindingContext.Result = ModelBindingResult.Success(null);
                return Task.CompletedTask;
            }

            bindingContext.ModelState.TryAddModelError(modelName, "O valor não pode ser vazio.");
            return Task.CompletedTask;
        }

        // ── Normaliza vírgula para ponto antes do parse, e SEMPRE usa
        //    InvariantCulture, ignorando a cultura do sistema operacional ──
        var valorNormalizado = valorBruto.Trim().Replace(",", ".");

        if (decimal.TryParse(valorNormalizado, NumberStyles.Number, CultureInfo.InvariantCulture, out var valorConvertido))
        {
            bindingContext.Result = ModelBindingResult.Success(valorConvertido);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(
                modelName,
                $"O valor '{valorBruto}' não é um número decimal válido.");
        }

        return Task.CompletedTask;
    }
}