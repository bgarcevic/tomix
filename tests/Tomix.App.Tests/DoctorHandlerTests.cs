using Tomix.App.Doctor;
using Tomix.Core.Doctor;

namespace Tomix.App.Tests;

public sealed class DoctorHandlerTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.1.0-alpha.1")]
    [InlineData("2.3.4-beta.5")]
    public void Handle_ReturnsDoctorResult(string version)
    {
        var handler = new DoctorHandler();

        var result = handler.Handle(version);

        Assert.NotNull(result.Data);
        Assert.Equal(version, result.Data.Version);
        Assert.NotEmpty(result.Data.Checks);
    }

    [Fact]
    public void Handle_IncludesRuntimeCheck()
    {
        var handler = new DoctorHandler();

        var result = handler.Handle("1.0.0");

        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "runtime" &&
            check.Status == DoctorCheckStatus.Pass);
    }
}
