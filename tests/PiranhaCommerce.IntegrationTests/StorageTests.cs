using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Storage.AzureBlob;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>File keys are the one thing a store must never trust; and the Azure Blob store against the Azurite emulator.</summary>
public class StorageTests
{
    [Theory]
    [InlineData("products/3f2c/original")]
    [InlineData("a/b-c_d.e/w160")]
    public void Ordinary_keys_are_accepted(string key) => Assert.Equal(key, FileKeys.Validate(key));

    [Theory]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/rooted")]
    [InlineData("trailing/")]
    [InlineData("a//b")]
    [InlineData("a\b")]
    [InlineData("C:/windows")]
    [InlineData("a b")]
    [InlineData("caf\u00e9")]
    public void Keys_that_could_leave_the_folder_or_confuse_a_store_are_refused(string key) => Assert.Throws<ArgumentException>(() => FileKeys.Validate(key));

    [Fact]
    public void With_no_storage_plugin_every_file_call_says_so()
    {
        var services = new ServiceCollection();
        PiranhaCommerce.Core.DependencyInjection.PiranhaCommerceCoreServiceCollectionExtensions.AddPiranhaCommerceCore(services);
        var store = services.BuildServiceProvider().GetRequiredService<IFileStore>();
        Assert.Throws<InvalidOperationException>(() => store.OpenAsync("a/b").GetAwaiter().GetResult());
    }

    /// <summary>The connection string of the Azurite emulator CI starts, or the developer's own (the standard local port); null when there is none to talk to.</summary>
    private static string? Azurite()
    {
        if (Environment.GetEnvironmentVariable("PIRANHACOMMERCE_TEST_AZURITE") is { Length: > 0 } configured) return configured; // CI sets this: it must then work
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("127.0.0.1", 10000).Wait(500) ? "UseDevelopmentStorage=true" : null;
        }
        catch { return null; }
    }

    [Fact]
    public async Task The_azure_blob_store_saves_opens_overwrites_and_deletes_a_folder()
    {
        if (Azurite() is not { } connection) return; // no emulator on this machine; CI always has one

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PiranhaCommerce:Storage:AzureBlob:ConnectionString"] = connection, ["PiranhaCommerce:Storage:AzureBlob:Container"] = "test-" + Guid.NewGuid().ToString("N")[..12],
        }).Build());
        new AzureBlobStoragePlugin().ConfigureServices(services);
        var store = services.BuildServiceProvider().GetRequiredService<IFileStore>();

        Assert.Null(await store.OpenAsync("products/a/1/original")); // nothing there yet (and the container is made on first use)
        await store.SaveAsync("products/a/1/original", new MemoryStream("first"u8.ToArray()));
        await store.SaveAsync("products/a/1/w160", new MemoryStream("small"u8.ToArray()));
        await store.SaveAsync("products/a/2/original", new MemoryStream("other"u8.ToArray()));
        await store.SaveAsync("products/ab/1/original", new MemoryStream("neighbour"u8.ToArray()));
        await store.SaveAsync("products/a/1/original", new MemoryStream("second"u8.ToArray())); // replaces

        async Task<string?> Text(string key)
        {
            await using var s = await store.OpenAsync(key);
            return s is null ? null : await new StreamReader(s).ReadToEndAsync();
        }
        Assert.Equal("second", await Text("products/a/1/original"));

        await store.DeleteFolderAsync("products/a/1");
        Assert.Null(await Text("products/a/1/original"));
        Assert.Null(await Text("products/a/1/w160"));
        Assert.Equal("other", await Text("products/a/2/original")); // a sibling folder is untouched
        Assert.Equal("neighbour", await Text("products/ab/1/original")); // and so is one whose name merely starts the same
        await store.DeleteFolderAsync("products/a/1"); // already gone: not an error

        await store.DeleteFolderAsync("products");
        Assert.Null(await Text("products/a/2/original"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("../x", new MemoryStream()));
    }
}
