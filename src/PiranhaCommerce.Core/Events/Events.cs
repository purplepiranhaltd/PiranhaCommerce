namespace PiranhaCommerce.Core.Events;

/// <summary>
/// Lets a service say that something happened ("order.placed") so other systems can be told, when a package that sends events (the Webhooks package)
/// is installed. Optional: when none is registered the services skip it, exactly like <see cref="Audit.IAuditTrail"/>.
/// <para>
/// <see cref="Publish"/> only adds the event to the current database context: it is saved by the SaveChanges that the caller makes next, inside the same
/// transaction as the change itself. So an event exists if and only if its change was committed, and a change that is rolled back or retried leaves none.
/// The caller must make sure a SaveChanges follows.
/// </para>
/// </summary>
public interface IEventPublisher
{
    /// <param name="type">For example <c>order.placed</c>. Lower case, words separated by dots or underscores.</param>
    /// <param name="data">Kept small: ids, reference, state and money figures. Anyone who needs more fetches it from the API. Serialised with the API's JSON rules (camelCase, enums as text).</param>
    void Publish(string type, object data);
}

/// <summary>One kind of event a package can send, listed so an administrator can choose which ones an endpoint receives.</summary>
public sealed record EventTypeInfo(string Type, string Description);

/// <summary>A package registers one of these to list the event types it publishes.</summary>
public interface IEventTypeContributor
{
    IEnumerable<EventTypeInfo> GetEventTypes();
}
