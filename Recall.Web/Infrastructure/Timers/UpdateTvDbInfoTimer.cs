//-----------------------------------------------------------------------
// <copyright file="UpdateTvDbInfoTimer.cs" company="Kevant Development">
//     Copyright (c) Kevant Development. All rights reserved.
// </copyright>
// <author>Joakim Fredlund</author>
//-----------------------------------------------------------------------

using Quartz;

namespace Recall.Web.Infrastructure.Timers;

[DisallowConcurrentExecution]
public class UpdateTvDbInfoTimer(ILogger<UpdateTvDbInfoTimer> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("Executing UpdateTvDbInfoTimer.Execute");
//            string fullPathName = options.Value.Configuration?.LogFilePath!;
//            RemoveOldFiles(fullPathName);

//            await liveMessageManager.RemoveOldLiveMessagesAsync();

//            RemoveOldCaptchaFiles();
    }
}
