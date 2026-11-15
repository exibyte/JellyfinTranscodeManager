using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TranscodeManager;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<MediaTools>();
        serviceCollection.AddSingleton<CandidateService>();
        serviceCollection.AddSingleton<TranscodeWorker>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TranscodeWorker>());
    }
}
