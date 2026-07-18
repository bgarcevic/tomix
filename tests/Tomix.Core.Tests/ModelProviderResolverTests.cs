using Tomix.Core.Models;

namespace Tomix.Core.Tests;

public sealed class ModelProviderResolverTests
{
    [Fact]
    public void ResolveSingle_NoProviderClaims_ReturnsNull()
    {
        var providers = new IModelProvider[] { new StubProvider(claims: false), new StubProvider(claims: false) };

        Assert.Null(providers.ResolveSingle(new ModelReference("model")));
    }

    [Fact]
    public void ResolveSingle_ExactlyOneClaims_ReturnsIt()
    {
        var expected = new StubProvider(claims: true);
        var providers = new IModelProvider[] { new StubProvider(claims: false), expected };

        Assert.Same(expected, providers.ResolveSingle(new ModelReference("model")));
    }

    [Fact]
    public void ResolveSingle_MultipleClaim_ThrowsWithEveryClaimant()
    {
        // Overlapping CanOpen contracts are a registration bug; they must surface instead of
        // being resolved silently by list order.
        var providers = new IModelProvider[]
        {
            new StubProvider(claims: true),
            new StubProvider(claims: false),
            new OtherStubProvider(),
        };

        var ex = Assert.Throws<AmbiguousModelProviderException>(
            () => providers.ResolveSingle(new ModelReference("model")));

        Assert.Contains("model", ex.Message);
        Assert.Contains(nameof(StubProvider), ex.Message);
        Assert.Contains(nameof(OtherStubProvider), ex.Message);
    }

    private sealed class StubProvider(bool claims) : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => claims;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class OtherStubProvider : IModelProvider
    {
        public bool CanOpen(ModelReference reference) => true;
        public Task<IModelSession> OpenAsync(ModelReference reference, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
