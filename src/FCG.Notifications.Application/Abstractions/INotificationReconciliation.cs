using FCG.Notifications.Application.Notifications;

namespace FCG.Notifications.Application.Abstractions;

/// <summary>Trusted internal administrative caller only. No HTTP authorization is provided here.</summary>
public interface INotificationReconciliation
{
    Task<NotificationReconciliationResult> ReconcileAsync(NotificationReconciliationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Trusted infrastructure integration, NOT a client-supplied boolean. Must prove every earlier
/// dispatcher process has terminated and prevent restarts until the returned lease is disposed.
/// The production implementation is deliberately absent: unknown resends fail closed.
/// </summary>
public interface INotificationReplaySafety
{
    Task<IAsyncDisposable> AcquireQuiescenceAsync(string evidenceReference, CancellationToken cancellationToken);
}
