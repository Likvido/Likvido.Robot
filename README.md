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
