//-----------------------------------------------------------------------
// <copyright file="IMailService.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

namespace Recall.Web.Services;

/// <summary>
/// The outbound mail queue. Callers <see cref="QueueEmailAsync"/> a message and
/// return immediately; the <c>MailTimer</c> Quartz job later calls
/// <see cref="SendPendingEmailsAsync"/> to deliver it.
/// </summary>
public interface IMailService
{
    /// <summary>
    /// Adds a message to the queue. It is committed immediately and picked up on
    /// the next <see cref="SendPendingEmailsAsync"/> run — this method never talks
    /// to an SMTP server itself, so callers don't block on delivery.
    /// </summary>
    Task QueueEmailAsync(
        string to,
        string subject,
        string body,
        int priority = MailService.NormalPriority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends up to <c>MailOptions.BatchSize</c> pending messages. A failed send is
    /// logged and its attempt count bumped; the message is retried on a later run
    /// until it succeeds or hits <c>MailOptions.MaxSendAttempts</c>.
    /// </summary>
    Task SendPendingEmailsAsync(CancellationToken cancellationToken = default);
}
