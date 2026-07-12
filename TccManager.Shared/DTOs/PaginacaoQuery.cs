namespace TccManager.Shared.DTOs;

public class PaginacaoQuery
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Page { get; set; } = DefaultPage;
    public int PageSize { get; set; } = DefaultPageSize;
}
