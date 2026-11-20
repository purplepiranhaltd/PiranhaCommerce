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

    [Fact]
    public void Duplicate_plugin_is_rejected()
    {
        var b = new ServiceCollection().AddPiranhaCore().AddPlugin<FakePlugin>();
        Assert.Throws<InvalidOperationException>(() => b.AddPlugin<FakePlugin>());
    }
}
