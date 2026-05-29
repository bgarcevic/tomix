using Mdl.App.Doctor;
using Mdl.Core.Doctor;

namespace Mdl.App.Tests;

public sealed class DoctorHandlerTests
{
    [Fact]
    public void Handle_ReturnsDoctorResult()
    {
        var handler = new DoctorHandler();

        var result = handler.Handle("0.1.0-test");

        Assert.NotNull(result.Data);
        Assert.Equal("0.1.0-test", result.Data.Version);
        Assert.NotEmpty(result.Data.Checks);
    }

    [Fact]
    public void Handle_IncludesRuntimeCheck()
    {
        var handler = new DoctorHandler();

        var result = handler.Handle("0.1.0-test");

        Assert.Contains(result.Data!.Checks, check =>
            check.Name == "runtime" &&
            check.Status == DoctorCheckStatus.Pass);
    }
}