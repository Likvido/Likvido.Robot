# Likvido.Robot
Helper library for creating robots

To create a robot, you need to implement a very simple interface:

```csharp
public class MyRobotEngine : ILikvidoRobotEngine
{
    public Task Run(CancellationToken cancellationToken)
    {
        // Your robot code here
    }
}
```

Then you can run the robot using this static helper method

```csharp
await RobotOperation.Run<MyRobotEngine>(
    "my-robot-name",
    (configuration, services) =>
    {
        // Add your services here
    }
);
```

## Exit code

The process exits with code `1` when the run does not finish:

1. `Run` throws.
2. A shutdown (such as SIGTERM) cancels the token, and `Run` throws an `OperationCanceledException` because of it.
3. `Run` ignores the cancelled token and is still running when the host's shutdown timeout ends.
4. A shutdown arrives while the host is still starting, before `Run` has started.

Kubernetes and Argo CD then see the Job as failed. A run that returns normally exits with code `0`, even after a
shutdown.
