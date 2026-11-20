using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Core.Tests;

public class PluginTests
{
    private sealed class FakePlugin : IPiranhaPlugin
    {
        public PluginDescriptor Descriptor { get; } = new("Fake", "Fake", "1.0");
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public void Registered_plugin_appears_in_catalog()
    {
        var services = new ServiceCollection();
        services.AddPiranhaCore().AddPlugin<FakePlugin>();
        using var sp = services.BuildServiceProvider();
        Assert.Single(sp.GetRequiredService<IPluginCatalog>().Plugins);
    }

    private sealed class DependentPlugin : IPiranhaPlugin
    {
        public PluginDescriptor Descriptor { get; } = new("Dependent", "Dependent", "1.0", DependsOn: ["Fake"]);
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public void Plugin_with_missing_dependency_is_rejected_with_a_clear_message()
    {
        var builder = new ServiceCollection().AddPiranhaCore();
        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddPlugin<DependentPlugin>());
        Assert.Contains("requires plugin 'Fake'", ex.Message);
    }

    [Fact]
    public void Plugin_is_accepted_once_its_dependency_is_registered()
    {
        var builder = new ServiceCollection().AddPiranhaCore().AddPlugin<FakePlugin>().AddPlugin<DependentPlugin>();
        Assert.Equal(2, builder.Plugins.Count);
    }

    [Fact]
    public void Duplicate_plugin_is_rejected()
    {
        var b = new ServiceCollection().AddPiranhaCore().AddPlugin<FakePlugin>();
        Assert.Throws<InvalidOperationException>(() => b.AddPlugin<FakePlugin>());
    }
}
