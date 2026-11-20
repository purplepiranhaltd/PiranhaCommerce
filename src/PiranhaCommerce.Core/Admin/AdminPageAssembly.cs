using System.Reflection;

namespace PiranhaCommerce.Core.Admin;

/// <summary>
/// Register one of these (<c>services.AddSingleton(new AdminPageAssembly(typeof(MyPlugin).Assembly))</c>) so the admin
/// router and endpoint mapping discover the routable Razor components (<c>@page</c>) in a plugin's assembly.
/// Without it the plugin's menu entries would appear but its pages would 404.
/// </summary>
public sealed record AdminPageAssembly(Assembly Assembly);
