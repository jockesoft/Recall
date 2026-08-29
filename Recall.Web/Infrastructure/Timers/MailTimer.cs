//-----------------------------------------------------------------------
// <copyright file="MailTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Quartz;
using Recall.Web.Services;

namespace Recall.Web.Infrastructure.Timers;

/// <summary>
/// Drains the outbound mail queue. Scheduled once a minute in
/// <c>Program.cs</c>; each run hands off to
/// <see cref="MailService.SendPendingEmailsAsync"/>, which sends at most one
/// batch and bumps the retry count on any message that fails.
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps a slow SMTP round
/// trip from letting two runs send the same message twice.
/// </summary>
[DisallowConcurrentExecution]
public sealed class MailTimer(IMailService mailService, ILogger<MailTimer> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await mailService.SendPendingEmailsAsync(context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let an unhandled exception escape into the scheduler.
            logger.LogError(ex, "MailTimer: unexpected failure while sending pending emails.");
        }
    }
}
