using TccManager.Api.Services;
using TccManager.Api.Validators;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.Validators;

public class AgendarBancaDtoValidatorTests
{
    private const string MensagemEsperada = "A data e hora da banca devem ser futuras.";

    private static AgendarBancaDto DtoComData(DateTime dataHora) => new()
    {
        DataHora = dataHora,
        Local = "Sala 101",
        ProfessoresIds = new List<int> { 21, 22 },
        MembrosExternosIds = new List<int>()
    };

    [Fact]
    public void DataClaramenteNoFuturo_DeveSerValida()
    {
        // "Agora" fixado no início de 2026; banca marcada para meados de 2026.
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var validator = new AgendarBancaDtoValidator(timeProvider);

        var dto = DtoComData(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Unspecified));

        var result = validator.Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void DataClaramenteNoPassado_DeveSerInvalida_ComPropriedadeEMensagemCertas()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var validator = new AgendarBancaDtoValidator(timeProvider);

        var dto = DtoComData(new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Unspecified));

        var result = validator.Validate(dto);

        Assert.False(result.IsValid);
        var erro = Assert.Single(result.Errors);
        Assert.Equal(nameof(AgendarBancaDto.DataHora), erro.PropertyName);
        Assert.Equal(MensagemEsperada, erro.ErrorMessage);
    }

    [Fact]
    public void DataUmSegundoNoFuturo_DeveSerValida()
    {
        // Fronteira: a data (em Brasília) é convertida para UTC e comparada com o "agora".
        // Derivamos o "agora" do próprio horário convertido para não depender do offset atual.
        var dataHoraBrasilia = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var utcDaBanca = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dataHoraBrasilia);

        var agoraUmSegundoAntes = new DateTimeOffset(utcDaBanca.AddSeconds(-1), TimeSpan.Zero);
        var validator = new AgendarBancaDtoValidator(new FixedTimeProvider(agoraUmSegundoAntes));

        var result = validator.Validate(DtoComData(dataHoraBrasilia));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void DataExatamenteIgualAoAgora_DeveSerInvalida()
    {
        // A regra usa comparação estrita (>), então a fronteira exata é inválida.
        var dataHoraBrasilia = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var utcDaBanca = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dataHoraBrasilia);

        var validator = new AgendarBancaDtoValidator(
            new FixedTimeProvider(new DateTimeOffset(utcDaBanca, TimeSpan.Zero)));

        var result = validator.Validate(DtoComData(dataHoraBrasilia));

        Assert.False(result.IsValid);
        Assert.Equal(nameof(AgendarBancaDto.DataHora), Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void DataUmSegundoNoPassado_DeveSerInvalida()
    {
        var dataHoraBrasilia = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var utcDaBanca = BrasiliaTimeZoneService.ConverterDeBrasiliaParaUtc(dataHoraBrasilia);

        var agoraUmSegundoDepois = new DateTimeOffset(utcDaBanca.AddSeconds(1), TimeSpan.Zero);
        var validator = new AgendarBancaDtoValidator(new FixedTimeProvider(agoraUmSegundoDepois));

        var result = validator.Validate(DtoComData(dataHoraBrasilia));

        Assert.False(result.IsValid);
    }
}
