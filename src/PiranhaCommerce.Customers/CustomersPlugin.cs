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

        services.AddOptions<CustomerAccountOptions>().BindConfiguration(CustomerAccountOptions.SectionName);
        services.AddScoped<ICustomerAccountService, CustomerAccountService>();
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

        b.Entity<CustomerErasure>(e =>
        {
            e.ToTable("CustomerErasure", Schema);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Details).HasMaxLength(500);
            e.HasIndex(x => x.CustomerId);
        });

        b.Entity<CustomerLogin>(e =>
        {
            e.ToTable("CustomerLogin", Schema);
            e.HasKey(l => l.CustomerId);
            e.Property(l => l.PasswordHash).HasMaxLength(512).IsRequired();
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

        customers.MapPut("/{id:guid}", async (Guid id, CustomerInput input, ICustomerService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } c ? Results.Ok(c) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        customers.MapDelete("/{id:guid}", async (Guid id, ICustomerService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
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
