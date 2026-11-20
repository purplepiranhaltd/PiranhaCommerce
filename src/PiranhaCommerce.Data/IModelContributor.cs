using Microsoft.EntityFrameworkCore;

namespace PiranhaCommerce.Data;

/// <summary>Packages/plugins implement this to add their entities and mappings to the shared DbContext.</summary>
public interface IModelContributor
{
    void OnModelCreating(ModelBuilder modelBuilder);
}
