using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableFunctionRegistryTests
{
    [Fact]
    public void Register_AddsFunctionByName()
    {
        var registry = new DurableFunctionRegistry();
        var func = AIFunctionFactory.Create(() => "test", "MyFunc");

        registry.Register(func);

        Assert.True(registry.ContainsKey("MyFunc"));
        Assert.Same(func, registry["MyFunc"]);
    }

    [Fact]
    public void Register_IsCaseInsensitive()
    {
        var registry = new DurableFunctionRegistry();
        var func = AIFunctionFactory.Create(() => "test", "MyFunc");

        registry.Register(func);

        Assert.True(registry.ContainsKey("myfunc"));
        Assert.True(registry.ContainsKey("MYFUNC"));
    }

    [Fact]
    public void Register_ThrowsOnNull()
    {
        var registry = new DurableFunctionRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void Constructor_WithConfigurators_RegistersFunctions()
    {
        var func = AIFunctionFactory.Create(() => "test", "Configured");
        Action<DurableFunctionRegistry>[] configurators =
        [
            reg => reg.Register(func)
        ];

        var registry = new DurableFunctionRegistry(configurators);

        Assert.True(registry.ContainsKey("Configured"));
    }

    [Fact]
    public void Constructor_WithNullConfigurators_CreatesEmptyRegistry()
    {
        var registry = new DurableFunctionRegistry(null);
        Assert.Empty(registry);
    }
}
