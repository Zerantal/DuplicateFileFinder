using System;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public static class PluginTestUtil
{
    public static async Task PostAndWaitAsync<TPlugin>(
        TPlugin plugin,
        RepoEvent evt,
        Func<bool>? predicate = null,
        int timeoutMs = 2000) where TPlugin : ChannelRepoPlugin
    {
        plugin.Post(evt);

        // Always wait for the plugin to finish processing the posted event(s).
        await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

        if (predicate == null)
            return;

        var stop = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < stop)
        {
            if (predicate())
                return;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Timed out waiting for predicate after plugin became ready.");
    }
}
