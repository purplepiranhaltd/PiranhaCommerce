using System.Reflection;

namespace PiranhaCommerce.Core.Identity;

/// <summary>
/// Marks a service method that changes something, and names the permission a member of staff needs to call it from the admin.
/// The admin runs every service call through <c>AdminScopeRunner</c>, which refuses the call (with <see cref="Validation.PiranhaCommerceForbiddenException"/>)
/// when the signed-in person lacks it, so a page cannot forget to check. Reading methods carry no attribute: the page's own permission covers them.
/// Put it on the interface method (the admin sees the service through its interface).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>Wraps a service so that each call is checked against what the signed-in person may do.</summary>
public static class PermissionGuard
{
    public static T Wrap<T>(T target, StaffAccess access) where T : notnull
    {
        if (!typeof(T).IsInterface) return target; // nothing to intercept on a class: it keeps only the page-level check
        var proxy = DispatchProxy.Create<T, Proxy<T>>();
        var p = (Proxy<T>)(object)proxy;
        p.Target = target;
        p.Access = access;
        return proxy;
    }

    /// <summary>The permission a call needs, or null for a call that needs none beyond the page's.</summary>
    public static string? Required(MethodInfo method) =>
        method.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission
        ?? method.DeclaringType?.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission;

    public class Proxy<T> : DispatchProxy where T : notnull
    {
        internal T Target { get; set; } = default!;
        internal StaffAccess Access { get; set; } = StaffAccess.None;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (Required(targetMethod) is { } key && !Access.Has(key))
                throw new Validation.PiranhaCommerceForbiddenException("You do not have permission to do that.");
            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
