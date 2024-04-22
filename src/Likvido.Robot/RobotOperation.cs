using Likvido.ApplicationInsights.Telemetry;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Likvido.Robot;

public static class RobotOperation
{
    public static async Task Run<T>(
        string name, 
        Action<IConfiguration, IServiceCollection> configureServices,
        Action<ILoggingBuilder>? configureLogging = null)
        where T : ILikvidoRobotEngine
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true)
            .Build();

        var serviceCollection = new ServiceCollection()
            .AddSingleton<ITelemetryInitializer>(new ServiceNameInitializer(name))
            .AddSingleton<ITelemetryInitializer>(new AvoidRequestSamplingTelemetryInitializer(name))
            .AddApplicationInsightsTelemetryWorkerService(configuration)
            .AddLogging(builder =>
            {
                builder.AddFilter("Azure", LogLevel.Warning);
                builder.AddFilter("Microsoft", LogLevel.Warning);
                builder.AddConsole();

                configureLogging?.Invoke(builder);
            });

        configureServices(configuration, serviceCollection);

        var serviceProvider = serviceCollection.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<T>();
        await RunOperationWithApplicationInsights(serviceProvider, name, engine.Run).ConfigureAwait(false);
    }

    private static async Task RunOperationWithApplicationInsights(IServiceProvider services, string name, Func<Task> engineFunc)
    {
        var func = async () =>
        {
            try
            {
                await engineFunc().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var logger = services.GetRequiredService<ILogger>();
                logger.LogError(e, $"Job run failed. Robot - {name}");
                throw;
            }
        };

        var telemetryClient = services.GetRequiredService<TelemetryClient>();
        await telemetryClient.ExecuteAsRequestAsync(new ExecuteAsRequestAsyncOptions(name, func)).ConfigureAwait(false);
    }
}
