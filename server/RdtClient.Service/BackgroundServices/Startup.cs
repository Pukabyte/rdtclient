using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RdtClient.Data.Data;
using RdtClient.Service.Services;
using Serilog;

namespace RdtClient.Service.BackgroundServices;

public class Startup : IHostedService
{
    public static Boolean Ready { get; private set; }

    private readonly IServiceProvider _serviceProvider;

    public Startup(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        Log.Warning($"Starting host on version {version}");

        using var scope = _serviceProvider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<DataContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var settings = scope.ServiceProvider.GetRequiredService<Settings>();
        await settings.Seed();
        await settings.ResetCache();

        var exampleConfigPath = "/data/db/instances.json.example";
        if (!File.Exists(exampleConfigPath))
        {
            var directoryPath = Path.GetDirectoryName(exampleConfigPath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var defaultConfig = new
            {
                Category = new { Host = "http://host:port", ApiKey = "api_key" },
                OtherCategory = new { Host = "http://other_host:port", ApiKey = "other_api_key" }
            };
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(exampleConfigPath, json, cancellationToken);
        }

        Ready = true;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}