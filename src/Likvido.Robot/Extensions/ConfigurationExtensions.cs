using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;

namespace Likvido.Robot.Extensions;

// Small helper extensions that are useful for almost all robots

[PublicAPI]
public static class ConfigurationExtensions
{
    public static string GetRequiredValue(this IConfiguration configuration, string key) =>
        configuration.GetValue<string>(key) ?? throw new InvalidOperationException($"Missing required configuration value for key: {key}");

    public static T GetRequiredValue<T>(this IConfiguration configuration, string key) =>
        configuration.GetValue<T>(key) ?? throw new InvalidOperationException($"Missing required configuration value for key: {key}");
}
