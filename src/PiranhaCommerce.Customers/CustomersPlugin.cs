using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers;

/// <summary>Customers and address books, shared by every sales channel (Ecommerce today, EPOS later).</summary>
public sealed class CustomersPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Customers", "Customers", "0.1.0", "Customers shared by all sales channels.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CustomersModelContributor>();
        services.AddSingleton<IApiEndpointContributor, CustomerApiEndpoints>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<ICustomerErasureService, CustomerErasureService>();
        services.AddScoped<ICustomerGroupService, CustomerGroupService>();
        services.AddSingleton<IApiEndpointContributor, CustomerGroupEndpoints>();

        services.AddOptions<CustomerAccountOptions>().BindConfiguration(CustomerAccountOptions.SectionName);
        services.AddScoped<ICustomerAccountService, CustomerAccountService>();
        services.AddScoped<ICustomerSignInAdminService>(sp => (CustomerAccountService)sp.GetRequiredService<ICustomerAccountService>());
        services.AddSingleton<IStorefrontEndpointContributor, CustomerStorefrontEndpoints>();
        services.AddSingleton<PiranhaCommerce.Core.Email.IEmailTemplateContributor, CustomerEmailContributor>();
    }
}

internal sealed class CustomersModelContributor : IModelContributor
{
    public const string Schema = "customer";

    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.ToTable("Customer", Schema);
            e.Property(c => c.Email).HasMaxLength(256).IsRequired();
            e.Property(c => c.NormalizedEmail).HasMaxLength(256).IsRequired();
            e.Property(c => c.FirstName).HasMaxLength(100);
            e.Property(c => c.LastName).HasMaxLength(100);
            e.Property(c => c.Phone).HasMaxLength(40);
            e.Property(c => c.Notes).HasMaxLength(2000);
            e.HasIndex(c => c.NormalizedEmail).IsUnique(); // one customer per email, whatever the letter case, on every provider
        });

        b.Entity<CustomerGroup>(e =>
        {
            e.ToTable("CustomerGroup", Schema);
            e.Property(g => g.Name).HasMaxLength(100).IsRequired();
            e.Property(g => g.NormalizedName).HasMaxLength(100).IsRequired();
            e.Property(g => g.Description).HasMaxLength(500);
            e.HasIndex(g => g.NormalizedName).IsUnique();
        });

        b.Entity<CustomerGroupMember>(e =>
        {
            e.ToTable("CustomerGroupMember", Schema);
            e.Property(m => m.AddedBy).HasMaxLength(256);
            e.HasIndex(m => new { m.GroupId, m.CustomerId }).IsUnique();
            e.HasIndex(m => m.CustomerId);
            e.HasOne<CustomerGroup>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Customer>().WithMany().HasForeignKey(m => m.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerErasure>(e =>
        {
            e.ToTable("CustomerErasure", Schema);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Details).HasMaxLength(500);
            e.HasIndex(x => x.CustomerId);
        });

        b.Entity<CustomerSignInEvent>(e =>
        {
            e.ToTable("CustomerSignInEvent", Schema);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(32).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(200);
            e.HasIndex(x => new { x.CustomerId, x.OccurredUtc });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerLogin>(e =>
        {
            e.ToTable("CustomerLogin", Schema);
            e.HasKey(l => l.CustomerId);
            e.Property(l => l.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(l => l.SessionVersion).HasDefaultValue(0);
            e.HasOne<Customer>().WithOne().HasForeignKey<CustomerLogin>(l => l.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerToken>(e =>
        {
            e.ToTable("CustomerToken", Schema);
            e.Property(x => x.Purpose).HasMaxLength(32).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.CustomerId, x.CreatedUtc });
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerAddress>(e =>
        {
            e.ToTable("CustomerAddress", Schema);
            e.Property(a => a.Label).HasMaxLength(100);
            e.Property(a => a.Recipient).HasMaxLength(200);
            e.Property(a => a.Line1).HasMaxLength(200).IsRequired();
            e.Property(a => a.Line2).HasMaxLength(200);
            e.Property(a => a.City).HasMaxLength(100).IsRequired();
            e.Property(a => a.Region).HasMaxLength(100);
            e.Property(a => a.PostalCode).HasMaxLength(20);
            e.Property(a => a.CountryCode).HasMaxLength(2).IsRequired();
            e.HasIndex(a => a.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class CustomerApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var customers = api.MapGroup("/customers").WithTags("Customers");

        customers.MapGet("/", async (int? page, int? pageSize, string? search, ICustomerService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, search, ct)));

        customers.MapGet("/{id:guid}", async (Guid id, ICustomerService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());

        customers.MapPost("/", async (CustomerInput input, ICustomerService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/customers/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapPut("/{id:guid}", async (Guid id, CustomerInput input, HttpContext http, ICustomerService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } c ? Results.Ok(c) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapDelete("/{id:guid}", async (Guid id, ICustomerService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // Their sign-in, for staff: see it, unlock it, end its sessions, send a reset link (or first invitation), remove it. Disabling is the Active flag.
        customers.MapGet("/{id:guid}/sign-in", async (Guid id, ICustomerSignInAdminService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound());

        customers.MapGet("/{id:guid}/sign-in/history", async (Guid id, int? limit, ICustomerSignInAdminService svc, CancellationToken ct) =>
            await svc.HistoryAsync(id, limit ?? 50, ct) is { } h ? Results.Ok(h) : Results.NotFound());

        customers.MapPost("/{id:guid}/sign-in/unlock", async (Guid id, HttpContext http, ICustomerSignInAdminService svc, CancellationToken ct) =>
                await svc.UnlockAsync(id, http.User.FindFirst("email")?.Value, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapPost("/{id:guid}/sign-in/sign-out", async (Guid id, HttpContext http, ICustomerSignInAdminService svc, CancellationToken ct) =>
                await svc.SignOutEverywhereAsync(id, http.User.FindFirst("email")?.Value, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapPost("/{id:guid}/sign-in/reset-link", async (Guid id, HttpContext http, ICustomerSignInAdminService svc, CancellationToken ct) =>
                await svc.SendResetLinkAsync(id, http.User.FindFirst("email")?.Value, ct) is { } outcome ? Results.Ok(new ResetLinkResultDto(outcome == ResetLinkOutcome.Sent, outcome)) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapPost("/{id:guid}/sign-in/verify-email", async (Guid id, HttpContext http, ICustomerSignInAdminService svc, CancellationToken ct) =>
                await svc.MarkEmailVerifiedAsync(id, http.User.FindFirst("email")?.Value, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapDelete("/{id:guid}/sign-in", async (Guid id, HttpContext http, ICustomerSignInAdminService svc, CancellationToken ct) =>
                await svc.RemoveAsync(id, http.User.FindFirst("email")?.Value, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // Privacy: everything held about a customer, and erasing them. Orders are anonymised, never deleted (the shop keeps its accounts).
        customers.MapGet("/{id:guid}/export", async (Guid id, ICustomerErasureService svc, CancellationToken ct) =>
            await svc.ExportAsync(id, ct) is { } export ? Results.Ok(export) : Results.NotFound());

        customers.MapPost("/{id:guid}/erase", async (Guid id, EraseCustomerInput input, ICustomerErasureService svc, HttpContext http, CancellationToken ct) =>
                await svc.EraseAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var addresses = api.MapGroup("/customers/{customerId:guid}/addresses").WithTags("Customer addresses");

        addresses.MapGet("/", async (Guid customerId, ICustomerService svc, CancellationToken ct) =>
            await svc.ListAddressesAsync(customerId, ct) is { } list ? Results.Ok(list) : Results.NotFound());

        addresses.MapPost("/", async (Guid customerId, AddressInput input, ICustomerService svc, CancellationToken ct) =>
                await svc.AddAddressAsync(customerId, input, ct) is { } a
                    ? Results.Created($"/api/v1/customers/{customerId}/addresses/{a.Id}", a)
                    : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        addresses.MapPut("/{addressId:guid}", async (Guid customerId, Guid addressId, AddressInput input, ICustomerService svc, CancellationToken ct) =>
                await svc.UpdateAddressAsync(customerId, addressId, input, ct) is { } a ? Results.Ok(a) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        addresses.MapDelete("/{addressId:guid}", async (Guid customerId, Guid addressId, ICustomerService svc, CancellationToken ct) =>
                await svc.DeleteAddressAsync(customerId, addressId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
