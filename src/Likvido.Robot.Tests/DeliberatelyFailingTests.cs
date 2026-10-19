using Shouldly;
using Xunit;

namespace Likvido.Robot.Tests;

public sealed class DeliberatelyFailingTests
{
    [Fact]
    public void Fails_on_purpose_to_show_an_annotation()
    {
        (1 + 1).ShouldBe(3);
    }
}
