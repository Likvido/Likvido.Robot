using Grafana.OpenTelemetry;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;

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

        var runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var serviceCollection = new ServiceCollection()
            .AddLogging(builder =>
            {
                // add configuration first
                builder.AddConfiguration(configuration.GetSection("Logging"));

                // add default filters if not specified in configuration
                if (!configuration.GetSection("Logging:LogLevel:Azure").Exists())
                {
                    builder.AddFilter("Azure", LogLevel.Warning);
                }

                if (!configuration.GetSection("Logging:LogLevel:Microsoft").Exists())
                {
                    builder.AddFilter("Microsoft", LogLevel.Warning);
                }

                builder.AddConsole();

                if (runningInContainer)
                {
                    builder.AddOpenTelemetry(options =>
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
            })
            .AddScoped<T>();

        configureServices(configuration, serviceCollection);

        await using var serviceProvider = serviceCollection.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<T>();

        var cancellationTokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, _) => cancellationTokenSource.Cancel();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellationTokenSource.Cancel();

        await RunOperation(serviceProvider, robotName, operationName, engine.Run, cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private static async Task RunOperation(IServiceProvider services, string robotName, string operationName,
        Func<CancellationToken, Task> engineFunc, CancellationToken cancellationToken)
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
    }
}
