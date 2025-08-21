using Grafana.OpenTelemetry;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;

namespace Likvido.Robot;

[PublicAPI]
public static class RobotOperation
{
    public record RobotMetadata(string RobotName, string OperationName);

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

        builder.Services.AddSingleton(new RobotMetadata(robotName, operationName));
        builder.Services.AddScoped<T>();
        builder.Services.AddHostedService<RobotHostedService<T>>();

        // Register the robot passed services configuration
        configureServices(builder.Configuration, builder.Services);

        var runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
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

        if (runningInContainer)
        {
            builder.Logging.AddOpenTelemetry(options =>
            {
                options.UseGrafana(settings =>
                {
                    settings.ServiceName = robotName;
                    settings.ResourceAttributes.Add("k8s.pod.name", Environment.GetEnvironmentVariable("HOSTNAME"));
                    settings.ExporterSettings = new AgentOtlpExporter
                    {
                        Protocol = OtlpExportProtocol.Grpc,
                        Endpoint = new Uri("http://grafana-alloy-otlp.grafana-alloy.svc.cluster.local:4317")
                    };
                });
                options.IncludeScopes = true;
            });
        }

        var host = builder.Build();

        // Log startup
        var logger = host.Services.GetRequiredService<ILogger<RobotHostedService<T>>>();
        logger.LogInformation("Starting robot. Robot: {RobotName}. Operation: {OperationName}", robotName,
            operationName);

        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // This is expected during shutdown
            logger.LogInformation("Robot shutdown completed. Robot: {RobotName}. Operation: {OperationName}", robotName,
                operationName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Robot failed. Robot: {RobotName}. Operation: {OperationName}", robotName,
                operationName);
            throw;
        }
    }

    public class RobotHostedService<T> : BackgroundService
        where T : ILikvidoRobotEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly RobotMetadata _robotMetadata;
        private readonly IHostApplicationLifetime _lifeTime;

        public RobotHostedService(
            IServiceProvider serviceProvider,
            IHostApplicationLifetime lifetime,
            RobotMetadata robotMetadata)
        {
            _robotMetadata = robotMetadata;
            _serviceProvider = serviceProvider;
            _lifeTime = lifetime;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<T>();
                await engine.Run(stoppingToken);
                // Stop after launching and finishing since BackgroundService will not finish itself
                _lifeTime.StopApplication();
            }
            catch (OperationCanceledException)
            {
                var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("RobotOperation");
                logger.LogWarning("Job was cancelled. Robot: {RobotName}. Operation: {OperationName}",
                    _robotMetadata.RobotName, _robotMetadata.OperationName);
                _lifeTime.StopApplication();
            }
            catch (Exception exception)
            {
                var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("RobotOperation");
                logger.LogError(exception, "Job run failed. Robot: {RobotName}. Operation: {OperationName}",
                    _robotMetadata.RobotName, _robotMetadata.OperationName);
                throw;
            }
        }
    }
}
