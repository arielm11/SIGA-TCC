using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class FeedbackDtoValidator : AbstractValidator<FeedbackDto>
{
    public FeedbackDtoValidator()
    {
        RuleFor(dto => dto.Nota)
            .InclusiveBetween(0, 10)
            .When(dto => dto.Nota.HasValue)
            .WithMessage("A nota deve estar entre 0 e 10.");
    }
}
