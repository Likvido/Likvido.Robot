using JetBrains.Annotations;

namespace Likvido.Robot;

[PublicAPI]
public interface ILikvidoRobotEngine
{
    public Task Run(CancellationToken cancellationToken);
}
