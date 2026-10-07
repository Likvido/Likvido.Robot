using JetBrains.Annotations;
using Likvido.Identity;
using Likvido.Metadata;
using Likvido.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);

        builder.Services.TryAddNullPrincipalProvider();
        builder.Services.AddSingleton(new AppMetadata { AppName = robotName, OperationName = operationName });
        builder.Services.AddScoped<T>();
        var runState = new RobotRunState();
        builder.Services.AddSingleton(runState);
        builder.Services.AddHostedService<RobotHostedService<T>>();

        // Register the robot passed services configuration
        configureServices(builder.Configuration, builder.Services);

        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        if (!builder.Configuration.GetSection("Logging:LogLevel:Azure").Exists())
        {
            builder.Logging.AddFilter("Azure", LogLevel.Warning);
        }

        if (!builder.Configuration.GetSection("Logging:LogLevel:Microsoft").Exists())
        {
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        }

        builder.Logging.AddConsole();

        // Ships logs to the cluster's Grafana Alloy collector, and a no-op anywhere that is not a
        // deployed workload. See Likvido.Telemetry for how that is decided — and for why this used to
        // key off DOTNET_RUNNING_IN_CONTAINER, which is true inside every GitHub Actions job too.
        builder.Logging.AddLikvidoOtlpLogging(robotName);

        var host = builder.Build();

        // Log startup
        var logger = host.Services.GetRequiredService<ILogger<RobotHostedService<T>>>();
        logger.LogInformation("Starting robot. Robot: {RobotName}. Operation: {OperationName}", robotName,
            operationName);

        // Not RunAsync: it disposes the host, and with it the log exporter, before the lines below are written
        try
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
                await host.WaitForShutdownAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // This is expected during shutdown
                logger.LogInformation("Robot shutdown completed. Robot: {RobotName}. Operation: {OperationName}",
                    robotName, operationName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Robot failed. Robot: {RobotName}. Operation: {OperationName}", robotName,
                    operationName);
                throw;
            }

            // A shutdown during startup, or an engine that ignores its token past the shutdown timeout, leaves the
            // run unfinished
            if (!runState.Finished)
            {
                logger.LogWarning(
                    "Robot stopped before its run finished. Robot: {RobotName}. Operation: {OperationName}",
                    robotName, operationName);
                Environment.ExitCode = 1;
            }
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
    }

    private sealed class RobotRunState
    {
        public volatile bool Finished;
    }

    public class RobotHostedService<T>(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        AppMetadata appMetadata,
        ILogger<RobotHostedService<T>> logger)
        : BackgroundService
        where T : ILikvidoRobotEngine
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<T>();
                await engine.Run(stoppingToken);
                // Stop after launching and finishing since BackgroundService will not finish itself
                lifetime.StopApplication();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Job was cancelled. Robot: {RobotName}. Operation: {OperationName}",
                    appMetadata.AppName, appMetadata.OperationName);
                // A run cut short by shutdown did not finish, so it must not count as a completed Job
                Environment.ExitCode = 1;
                lifetime.StopApplication();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Job run failed. Robot: {RobotName}. Operation: {OperationName}",
                    appMetadata.AppName, appMetadata.OperationName);
                // The host only logs a faulted BackgroundService and stops; it never sets an exit code
                Environment.ExitCode = 1;
                lifetime.StopApplication();
            }
            finally
            {
                serviceProvider.GetService<RobotRunState>()?.Finished = true;
            }
        }
    }
}
