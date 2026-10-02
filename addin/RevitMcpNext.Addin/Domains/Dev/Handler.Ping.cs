using System;
using System.Collections.Generic;

namespace RevitMcpNext.Addin
{
    /// <summary>Internal diagnostics ops (dev.* keys are outside the catalog and skipped by the registry checks).</summary>
    internal static class DevPingOps
    {
        /// <summary>
        /// dev.ping: a queued no-op that proves the UI thread services the queue (wake latency probe for status
        /// detail:full and the V18 spike). Returns the queue wait and raise-to-execute times.
        /// </summary>
        [Op("dev.ping", Kind = "read", Scope = "none", Idle = true, Inproc = true)]
        public static OpResult Ping(RequestContext ctx)
        {
            QueuedRevitWorkItem item = ctx.WorkItem;
            var data = new Dictionary<string, object>
            {
                ["pong"] = true,
                ["atUtc"] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["via"] = ctx.Via,
                ["queueWaitMs"] = item?.QueueWaitMs ?? 0,
                ["raiseToExecMs"] = item?.RaiseToExecMs ?? 0,
                ["instanceId"] = ctx.Instance?.InstanceId,
                ["year"] = ctx.Year
            };
            return OpResult.Success(data, "pong from Revit " + ctx.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
