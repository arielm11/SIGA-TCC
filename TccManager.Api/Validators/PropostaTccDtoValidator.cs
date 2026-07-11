using FluentValidation;
using TccManager.Shared.DTOs;

namespace TccManager.Api.Validators;

public class PropostaTccDtoValidator : AbstractValidator<PropostaTccDto>
{
    public PropostaTccDtoValidator()
    {
    }
}
