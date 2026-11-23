using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Webhooks;

public sealed class WebhookOptions
{
    public const string SectionName = "Piranha:Webhooks";

    /// <summary>How often the background sender looks for events to send, in seconds. 0 turns the background sender off (events are still recorded).</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Attempts before a delivery is given up on and marked failed (it can still be sent again by hand).</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Events, and deliveries that are finished, are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Allow plain http:// addresses (otherwise only https://, with http allowed for localhost). For development only.</summary>
    public bool AllowInsecureUrls { get; set; }
}

/// <summary>Somewhere to send events: an address, the secret the messages are signed with, and which events it wants.</summary>
[Audited]
public class WebhookEndpoint : AuditableEntity
{
    public required string Url { get; set; }
    public string? Description { get; set; }

    /// <summary>The signing key. Shown once when made or changed, never again, and never written to the audit log.</summary>
    [AuditMask] public required string Secret { get; set; }

    /// <summary>Comma-separated event types, or <c>*</c> for all of them.</summary>
    public string EventTypes { get; set; } = "*";

    public bool Active { get; set; } = true;
}

/// <summary>Something that happened, written in the same transaction as the change. The sender turns it into one delivery per interested endpoint.</summary>
public class WebhookEvent : Entity
{
    public required string Type { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }

    /// <summary>The event's data, as JSON.</summary>
    public required string Payload { get; set; }

    /// <summary>True once its deliveries have been made (or it is a test, which is sent straight away).</summary>
    public bool FannedOut { get; set; }
}

public enum WebhookDeliveryStatus { Pending, Succeeded, Failed }

/// <summary>One event going to one endpoint, with how many times it was tried and what came back.</summary>
public class WebhookDelivery : Entity
{
    public Guid EventId { get; set; }
    public Guid EndpointId { get; set; }
    public required string EventType { get; set; }
    public WebhookDeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }

    /// <summary>While a sender is working on it, so another host does not send it too.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public string? LastResponse { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public bool IsTest { get; set; }
}

public sealed class WebhookEndpointInput
{
    /// <summary>Where to send events: https://, or http:// for localhost only.</summary>
    [Required, StringLength(500, MinimumLength = 8)] public string Url { get; set; } = "";
    [StringLength(200)] public string? Description { get; set; }

    /// <summary>Event types to receive. Empty or <c>*</c> means all of them.</summary>
    [MaxLength(100)] public List<string> EventTypes { get; set; } = [];
    public bool Active { get; set; } = true;
}

/// <param name="Secret">Only filled in when the endpoint is made or its secret is changed. Keep it: it is not shown again.</param>
public sealed record WebhookEndpointDto(
    Guid Id, string Url, string? Description, IReadOnlyList<string> EventTypes, bool Active, string SecretHint, DateTimeOffset CreatedUtc,
    string? Secret = null);

public sealed record WebhookDeliveryDto(
    Guid Id, Guid EventId, Guid EndpointId, string EventType, WebhookDeliveryStatus Status, int Attempts, DateTimeOffset NextAttemptUtc,
    int? LastStatusCode, string? LastError, string? LastResponse, DateTimeOffset CreatedUtc, DateTimeOffset? CompletedUtc, bool IsTest);

public sealed record WebhookEventTypeDto(string Type, string Description);

/// <param name="FannedOut">Events turned into deliveries.</param>
/// <param name="Delivered">Deliveries that succeeded.</param>
/// <param name="Retrying">Deliveries that failed and will be tried again.</param>
/// <param name="GaveUp">Deliveries that failed for the last time.</param>
public sealed record DispatchResult(int FannedOut, int Delivered, int Retrying, int GaveUp);
