namespace TccManager.Api.Services;

public static class BrasiliaTimeZoneService
{
    private static readonly TimeZoneInfo FusoBrasilia = ObterFusoBrasilia();

    private static TimeZoneInfo ObterFusoBrasilia()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
    }

    public static DateTime ConverterDeBrasiliaParaUtc(DateTime dataHoraLocal)
    {
        var dataHoraSemFuso = DateTime.SpecifyKind(dataHoraLocal, DateTimeKind.Unspecified);

        return TimeZoneInfo.ConvertTimeToUtc(dataHoraSemFuso, FusoBrasilia);
    }

    public static DateTime ConverterDeUtcParaBrasilia(DateTime dataHoraUtc)
    {
        var dataHoraUtcExplicita = DateTime.SpecifyKind(dataHoraUtc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(dataHoraUtcExplicita, FusoBrasilia);
    }
}