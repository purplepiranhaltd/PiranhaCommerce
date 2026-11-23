using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Events;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Webhooks;

/// <summary>
/// Tells other systems when things happen. Services that publish events (orders, payments, shipments, returns, disputes) write them in the same
/// transaction as the change; this package then sends each one, signed, to every endpoint that asked for it, retrying failures. Install it by adding this plugin.
/// </summary>
public sealed class WebhookPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Webhooks", "Webhooks", "0.1.0", "Signed, retried HTTP notifications about orders, payments and more.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, WebhookModelContributor>();
        services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.SectionName);
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<IWebhookService, WebhookService>();
        services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
        services.AddHttpClient(WebhookDispatcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }); // a redirect is a failure, never followed
        services.AddHostedService<WebhookDispatchService>();
        services.AddSingleton<IApiEndpointContributor, WebhookApiEndpoints>();
    }
}

internal sealed class WebhookModelContributor : IModelContributor
{
    private const string Schema = "webhooks";

    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<WebhookEndpoint>(e =>
        {
            e.ToTable("WebhookEndpoint", Schema);
            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Secret).HasMaxLength(100).IsRequired();
            e.Property(x => x.EventTypes).HasMaxLength(4000).IsRequired();
        });
        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("WebhookEvent", Schema);
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.HasIndex(x => new { x.FannedOut, x.OccurredUtc });
        });
        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("WebhookDelivery", Schema);
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.LastResponse).HasMaxLength(1000);
            e.HasIndex(x => new { x.Status, x.NextAttemptUtc });
            e.HasIndex(x => new { x.EndpointId, x.CreatedUtc });
            e.HasIndex(x => x.EventId);
            e.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class WebhookApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // Addresses, delivery logs and signing keys: administrators only, for reading as well.
        var g = api.MapGroup("/webhooks").WithTags("Webhooks").RequireAuthorization(ApiPolicies.Write);
        g.MapGet("/event-types", (IWebhookService svc) => Results.Ok(svc.EventTypes()));
        g.MapGet("/", async (IWebhookService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        g.MapGet("/{id:guid}", async (Guid id, IWebhookService svc, CancellationToken ct) => await svc.GetAsync(id, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        g.MapPost("/", async (WebhookEndpointInput input, IWebhookService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/v1/webhooks/{created.Id}", created);
        });
        g.MapPut("/{id:guid}", async (Guid id, WebhookEndpointInput input, IWebhookService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        g.MapDelete("/{id:guid}", async (Guid id, IWebhookService svc, CancellationToken ct) => await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/{id:guid}/rotate-secret", async (Guid id, IWebhookService svc, CancellationToken ct) =>
            await svc.RotateSecretAsync(id, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        g.MapPost("/{id:guid}/test", async (Guid id, IWebhookDispatcher dispatcher, CancellationToken ct) =>
            await dispatcher.SendTestAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        g.MapGet("/{id:guid}/deliveries", async (Guid id, WebhookDeliveryStatus? status, int? page, int? pageSize, IWebhookService svc, CancellationToken ct) =>
            await svc.ListDeliveriesAsync(id, status, page, pageSize, ct) is { } list ? Results.Ok(list) : Results.NotFound());
        api.MapPost("/webhook-deliveries/{id:guid}/redeliver", async (Guid id, IWebhookService svc, CancellationToken ct) =>
            await svc.RedeliverAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write).WithTags("Webhooks");
    }
}
