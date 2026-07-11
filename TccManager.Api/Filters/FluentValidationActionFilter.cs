using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace TccManager.Api.Filters;

/// <summary>
/// Executa a validação FluentValidation (2ª camada) para os argumentos da action que
/// possuírem um IValidator{T} registrado no container. Em caso de falha, os erros são
/// copiados para o ModelState e a resposta é gerada pela mesma fábrica configurada em
/// ApiBehaviorOptions.InvalidModelStateResponseFactory, garantindo o mesmo formato
/// (ValidationProblemDetails) já usado pelas DataAnnotations do [ApiController].
/// </summary>
public class FluentValidationActionFilter : IAsyncActionFilter
{
    private readonly IOptions<ApiBehaviorOptions> _apiBehaviorOptions;

    public FluentValidationActionFilter(IOptions<ApiBehaviorOptions> apiBehaviorOptions)
    {
        _apiBehaviorOptions = apiBehaviorOptions;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
                continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
                continue;

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext);

            if (!result.IsValid)
            {
                foreach (var error in result.Errors)
                {
                    context.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
                }
            }
        }

        if (!context.ModelState.IsValid)
        {
            context.Result = _apiBehaviorOptions.Value.InvalidModelStateResponseFactory(context);
            return;
        }

        await next();
    }
}
