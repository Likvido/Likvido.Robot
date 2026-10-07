using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

// Environment.ExitCode is process-wide, so no two tests may run a robot at the same time.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Likvido.Robot.Tests;

public sealed class RobotOperationExitCodeTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();

    public RobotOperationExitCodeTests() => Environment.ExitCode = 0;

    public void Dispose() => Environment.ExitCode = 0;

    [Fact]
    public async Task A_succeeding_run_exits_with_zero()
    {
        await RunRobot<SucceedingEngine>();

        Environment.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_run_exits_with_one_without_throwing()
    {
        await RunRobot<FailingEngine>();

        Environment.ExitCode.ShouldBe(1);
        _logs.Messages.ShouldContain(m => m.StartsWith("Job run failed"));
    }

    [Fact]
    public async Task A_timeout_is_a_failure_not_a_cancellation()
    {
        await RunRobot<TimingOutEngine>();

        Environment.ExitCode.ShouldBe(1);
        _logs.Messages.ShouldContain(m => m.StartsWith("Job run failed"));
        _logs.Messages.ShouldNotContain(m => m.StartsWith("Job was cancelled"));
    }

    [Fact]
    public async Task A_run_cut_short_by_shutdown_exits_with_one()
    {
        await RunRobot<ShutDownMidRunEngine>();

        Environment.ExitCode.ShouldBe(1);
        _logs.Messages.ShouldContain(m => m.StartsWith("Job was cancelled"));
    }

    [Fact]
    public async Task A_run_abandoned_at_the_shutdown_timeout_exits_with_one()
    {
        try
        {
            await RunRobot<TokenIgnoringEngine>(services =>
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            TokenIgnoringEngine.Release.TrySetResult();
        }

        Environment.ExitCode.ShouldBe(1);
        _logs.Messages.ShouldContain(m => m.StartsWith("Robot stopped before its run finished"));
    }

    [Fact]
    public async Task A_shutdown_before_the_run_starts_exits_with_one()
    {
        await RunRobot<SucceedingEngine>(services => services.AddHostedService<StopsDuringStartup>());

        Environment.ExitCode.ShouldBe(1);
        _logs.Messages.ShouldContain(m => m.StartsWith("Robot stopped before its run finished"));
    }

    private Task RunRobot<T>(Action<IServiceCollection>? configure = null) where T : class, ILikvidoRobotEngine =>
        RobotOperation.Run<T>("test-robot", (_, services) =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
            configure?.Invoke(services);
        });

    private sealed class SucceedingEngine : ILikvidoRobotEngine
    {
        public Task Run(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingEngine : ILikvidoRobotEngine
    {
        public async Task Run(CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("The run failed");
        }
    }

    private sealed class TimingOutEngine : ILikvidoRobotEngine
    {
        public async Task Run(CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new TaskCanceledException("A request timed out");
        }
    }

    private sealed class ShutDownMidRunEngine(IHostApplicationLifetime lifetime) : ILikvidoRobotEngine
    {
        public async Task Run(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class TokenIgnoringEngine(IHostApplicationLifetime lifetime) : ILikvidoRobotEngine
    {
        public static readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Run(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            await Release.Task;
        }
    }

    // Starting runs before any hosted service starts, so the robot's engine never gets to run
    private sealed class StopsDuringStartup(IHostApplicationLifetime lifetime) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._messages)
                {
                    provider._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
