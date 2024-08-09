using JetBrains.Annotations;
using Likvido.ApplicationInsights.Telemetry;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Likvido.Robot;

[PublicAPI]
public static class RobotOperation
{
    public static async Task Run<T>(
        string robotName,
        Action<IConfiguration, IServiceCollection> configureServices)
        where T : class, ILikvidoRobotEngine
    {
        await Run<T>(robotName, robotName, configureServices).ConfigureAwait(false);
    }

    public static async Task Run<T>(
        string robotName,
        string operationName,
        Action<IConfiguration, IServiceCollection> configureServices)
        where T : class, ILikvidoRobotEngine
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true)
            .Build();

        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            // make sure Application Insights configuration is present when running in a container
            if (string.IsNullOrWhiteSpace(configuration["ApplicationInsights:InstrumentationKey"]) ||
                string.IsNullOrWhiteSpace(configuration["ApplicationInsights:ConnectionString"]))
            {
                throw new InvalidOperationException("Application Insights configuration is missing. Please ensure the configuration is present in the appsettings.json file when running in a container.");
            }
        }

        var serviceCollection = new ServiceCollection()
            .AddSingleton<ITelemetryInitializer>(new ServiceNameInitializer(robotName))
            .AddSingleton<ITelemetryInitializer>(new AvoidRequestSamplingTelemetryInitializer(operationName))
            .AddApplicationInsightsTelemetryWorkerService(configuration)
            .AddLogging(builder =>
            {
                builder.AddFilter("Azure", LogLevel.Warning);
                builder.AddFilter("Microsoft", LogLevel.Warning);
                builder.AddConsole();
            })
            .AddScoped<T>();

        configureServices(configuration, serviceCollection);

        var serviceProvider = serviceCollection.BuildServiceProvider();

        using var scope = serviceProvider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<T>();

        var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, _) => cancellationTokenSource.Cancel();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellationTokenSource.Cancel();

        await RunOperationWithApplicationInsights(serviceProvider, robotName, operationName, cancellationTokenSource.Token, engine.Run).ConfigureAwait(false);
    }

    private static async Task RunOperationWithApplicationInsights(
        IServiceProvider services, string robotName, string operationName, CancellationToken cancellationToken, Func<CancellationToken, Task> engineFunc)
    {
        var func = async () =>
        {
            try
            {
                await engineFunc(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("RobotOperation");
                logger.LogWarning("Job was cancelled. Robot: {RobotName}. Operation: {OperationName}", robotName, operationName);
            }
            catch (Exception exception)
            {
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("RobotOperation");
                logger.LogError(exception, "Job run failed. Robot: {RobotName}. Operation: {OperationName}", robotName, operationName);
                throw;
            }
        };

        var telemetryClient = services.GetRequiredService<TelemetryClient>();
        await telemetryClient.ExecuteAsRequestAsync(new ExecuteAsRequestAsyncOptions(operationName, func) { FlushWait = null }).ConfigureAwait(false);
        await telemetryClient.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
