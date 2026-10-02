using DshPet.Core.Parsing;
using Xunit;

namespace DshPet.Core.Tests;

public class DeepSeekBalanceTests
{
    [Fact]
    public void Reads_the_CNY_total()
    {
        const string body = """
        {"is_available":true,"balance_infos":[
          {"currency":"CNY","total_balance":"32.66","granted_balance":"0.00","topped_up_balance":"32.66"}]}
        """;
        Assert.Equal(32.66, DeepSeekBalance.ParseTotalCny(body), 6);
    }

    [Fact]
    public void Picks_CNY_out_of_several_currencies()
    {
        const string body = """
        {"balance_infos":[
          {"currency":"USD","total_balance":"5.00"},
          {"currency":"CNY","total_balance":"18.25"}]}
        """;
        Assert.Equal(18.25, DeepSeekBalance.ParseTotalCny(body), 6);
    }

    [Fact]
    public void Accepts_a_bare_number_as_well_as_a_string()
    {
        const string body = """{"balance_infos":[{"currency":"CNY","total_balance":7.5}]}""";
        Assert.Equal(7.5, DeepSeekBalance.ParseTotalCny(body), 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"balance_infos":[]}""")]
    [InlineData("""{"balance_infos":[{"currency":"USD","total_balance":"1.00"}]}""")]
    [InlineData("""{"balance_infos":[{"currency":"CNY"}]}""")]
    public void Yields_NaN_instead_of_throwing(string body)
    {
        Assert.True(double.IsNaN(DeepSeekBalance.ParseTotalCny(body)), $"expected NaN for: {body}");
    }

    [Fact]
    public void Handles_null_input()
    {
        Assert.True(double.IsNaN(DeepSeekBalance.ParseTotalCny(null)));
    }
}
