# Likvido.Robot
Helper library for creating robots

To create a robot, you need to implement a very simple interface:

```csharp
public class MyRobotEngine : ILikvidoRobotEngine
{
    public Task Run()
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

By default, it will configure logging to the console and to Application Insights. If you wish to augment or change the logging setup, we do provide an optional action you can pass to do this:

```csharp
await RobotOperation.Run<MyRobotEngine>(
    "my-robot-name",
    (configuration, services) =>
    {
        // Add your services here
    },
    (loggingBuilder) =>
    {
        // Add your logging setup here
    }
);
```
