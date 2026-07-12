using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using TccManager.Api.ModelBinding;
using TccManager.Shared.DTOs;
using Xunit;

namespace TccManager.Tests.ModelBinding;

public class PaginacaoQueryModelBinderTests
{
    private static async Task<PaginacaoQuery> Bind(string? page, string? pageSize)
    {
        var binder = new PaginacaoQueryModelBinder();

        var valores = new Dictionary<string, string?>();
        if (page != null) valores["page"] = page;
        if (pageSize != null) valores["pageSize"] = pageSize;

        var context = new DefaultModelBindingContext
        {
            ValueProvider = new FakeValueProvider(valores)
        };

        await binder.BindModelAsync(context);

        Assert.True(context.Result.IsModelSet);
        return Assert.IsType<PaginacaoQuery>(context.Result.Model);
    }

    [Fact]
    public async Task ValoresValidos_SaoPreservados()
    {
        var query = await Bind("3", "25");

        Assert.Equal(3, query.Page);
        Assert.Equal(25, query.PageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-999")]
    public async Task PageMenorQueUm_ClampaParaUm(string page)
    {
        var query = await Bind(page, "20");

        Assert.Equal(PaginacaoQuery.DefaultPage, query.Page);
        Assert.Equal(1, query.Page);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task PageSizeMenorQueUm_ClampaParaDefault(string pageSize)
    {
        var query = await Bind("1", pageSize);

        Assert.Equal(PaginacaoQuery.DefaultPageSize, query.PageSize);
        Assert.Equal(20, query.PageSize);
    }

    [Theory]
    [InlineData("101", 100)]
    [InlineData("500", 100)]
    [InlineData("100", 100)]
    public async Task PageSizeAcimaDoMaximo_ClampaParaMaximo(string pageSize, int esperado)
    {
        var query = await Bind("1", pageSize);

        Assert.Equal(esperado, query.PageSize);
        Assert.True(query.PageSize <= PaginacaoQuery.MaxPageSize);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task PageNaoNumerico_ClampaParaDefaultSemErro(string page)
    {
        var query = await Bind(page, "20");

        Assert.Equal(PaginacaoQuery.DefaultPage, query.Page);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("10x")]
    [InlineData("")]
    public async Task PageSizeNaoNumerico_ClampaParaDefaultSemErro(string pageSize)
    {
        var query = await Bind("1", pageSize);

        Assert.Equal(PaginacaoQuery.DefaultPageSize, query.PageSize);
    }

    [Fact]
    public async Task ParametrosAusentes_UsamOsDefaults()
    {
        var query = await Bind(null, null);

        Assert.Equal(PaginacaoQuery.DefaultPage, query.Page);
        Assert.Equal(PaginacaoQuery.DefaultPageSize, query.PageSize);
    }

    [Fact]
    public async Task BinderNuncaAdicionaErroDeModelState()
    {
        var binder = new PaginacaoQueryModelBinder();
        var context = new DefaultModelBindingContext
        {
            ModelState = new ModelStateDictionary(),
            ValueProvider = new FakeValueProvider(new Dictionary<string, string?>
            {
                ["page"] = "nao-numerico",
                ["pageSize"] = "tambem-nao"
            })
        };

        await binder.BindModelAsync(context);

        Assert.True(context.ModelState.IsValid);
        Assert.Equal(0, context.ModelState.ErrorCount);
    }

    private sealed class FakeValueProvider : IValueProvider
    {
        private readonly Dictionary<string, string?> _valores;

        public FakeValueProvider(Dictionary<string, string?> valores) => _valores = valores;

        public bool ContainsPrefix(string prefix) => _valores.ContainsKey(prefix);

        public ValueProviderResult GetValue(string key)
        {
            if (_valores.TryGetValue(key, out var valor) && valor != null)
                return new ValueProviderResult(new StringValues(valor));

            return ValueProviderResult.None;
        }
    }
}
