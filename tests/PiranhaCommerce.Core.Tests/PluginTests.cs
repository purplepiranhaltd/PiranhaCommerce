using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Core.Tests;

public class PluginTests
{
    private class TestPlugin(string id, params IPiranhaPlugin[] requires) : IPiranhaPlugin
    {
        public int ConfigureCalls { get; private set; }
        public PluginInfo Info { get; } = new(id, id, "1.0");
        public IReadOnlyList<IPiranhaPlugin> Requires { get; } = requires;
        public void ConfigureServices(IServiceCollection services) => ConfigureCalls++;
    }

    private sealed class FakePlugin() : TestPlugin("Fake");

    private static PiranhaBuilder NewBuilder() => new ServiceCollection().AddPiranhaCore();

    [Fact]
    public void Registered_plugin_appears_in_catalog()
    {
        var services = new ServiceCollection();
        services.AddPiranhaCore().AddPlugin<FakePlugin>();
        using var sp = services.BuildServiceProvider();
        var plugin = Assert.Single(sp.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal("Fake", plugin.Id);
    }

    [Fact]
    public void Required_plugins_are_registered_automatically_and_first()
    {
        var core = new TestPlugin("Core");
        var mid = new TestPlugin("Mid", core);
        var top = new TestPlugin("Top", mid);

        var builder = NewBuilder().AddPlugin(top);

        Assert.Equal(["Core", "Mid", "Top"], builder.Plugins.Select(p => p.Info.Id));
        Assert.Equal(1, core.ConfigureCalls);
    }

    [Fact]
    public void A_plugin_needed_by_two_others_is_registered_once()
    {
        var shared = new TestPlugin("Shared");
        var builder = NewBuilder()
            .AddPlugin(new TestPlugin("A", shared))
            .AddPlugin(new TestPlugin("B", new TestPlugin("Shared")));   // a different instance with the same id

        Assert.Equal(["Shared", "A", "B"], builder.Plugins.Select(p => p.Info.Id));
        Assert.Equal(1, shared.ConfigureCalls);
    }

    [Fact]
    public void Registering_a_plugin_twice_does_nothing_the_second_time()
    {
        var builder = NewBuilder().AddPlugin<FakePlugin>().AddPlugin<FakePlugin>();
        Assert.Single(builder.Plugins);
    }

    [Fact]
    public void Circular_requirements_are_reported_not_a_stack_overflow()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewBuilder().AddPlugin(new CyclicPlugin("A", "B")));
        Assert.Contains("circular", ex.Message);
    }

    /// <summary>Requires a plugin "other" which requires a plugin "id", and so on forever.</summary>
    private sealed class CyclicPlugin(string id, string other) : IPiranhaPlugin
    {
        public PluginInfo Info { get; } = new(id, id, "1.0");
        public IReadOnlyList<IPiranhaPlugin> Requires => [new CyclicPlugin(other, id)];
        public void ConfigureServices(IServiceCollection services) { }
    }
}
