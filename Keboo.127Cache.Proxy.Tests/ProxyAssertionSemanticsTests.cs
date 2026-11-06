using Keboo._127Cache;

using Microsoft.Extensions.Caching.Memory;

using TUnit.Assertions.Exceptions;

namespace Keboo._127Cache.Proxy.Tests;

public sealed class ProxyAssertionSemanticsTests
{
    [Test]
    public async Task BuildEntryEqualityRejectsAliasedArtifactWithDifferentId()
    {
        var expected = CreateBuild();
        var equal = expected with
        {
            Packages = expected.Packages with { },
            VersionArtifact = expected.VersionArtifact! with { }
        };
        await ProxyAssertions.EqualRecords(new[] { equal }, new[] { expected });

        var aliased = expected with { VersionArtifact = expected.Packages };
        await Assert.That(async () => await ProxyAssertions.EqualRecords(new[] { aliased }, new[] { expected }))
            .ThrowsExactly<AssertionException>();

        var other = expected with { RunId = expected.RunId + 1 };
        await Assert.That(async () => await ProxyAssertions.EqualRecords(new[] { other, expected }, new[] { expected, other }))
            .ThrowsExactly<AssertionException>();
        await Assert.That(async () => await ProxyAssertions.EqualRecords(new[] { expected, expected }, new[] { expected }))
            .ThrowsExactly<AssertionException>();
    }

    [Test]
    public async Task PublishedPackageEqualityRejectsAliasedArtifactWithDifferentId()
    {
        var expected = new PublishedPackage("Example", CreateBuild());
        var equal = expected with
        {
            Build = expected.Build with
            {
                Packages = expected.Build.Packages with { },
                VersionArtifact = expected.Build.VersionArtifact! with { }
            }
        };
        await ProxyAssertions.EqualRecords(new[] { equal }, new[] { expected });

        var aliased = expected with { Build = expected.Build with { VersionArtifact = expected.Build.Packages } };
        await Assert.That(async () => await ProxyAssertions.EqualRecords(new[] { aliased }, new[] { expected }))
            .ThrowsExactly<AssertionException>();
    }

    [Test]
    public async Task ExactMemoryCacheRejectsDerivedInstances()
    {
        using var exact = new MemoryCache(new MemoryCacheOptions());
        await ProxyAssertions.ExactMemoryCache(exact);

        using var derived = new DerivedMemoryCache();
        await Assert.That(async () => await ProxyAssertions.ExactMemoryCache(derived))
            .ThrowsExactly<AssertionException>();
        await Assert.That(async () => await ProxyAssertions.ExactMemoryCache(null))
            .ThrowsExactly<AssertionException>();
    }

    private static BuildEntry CreateBuild()
    {
        var packages = new Artifact(2, "artifact", false, null);
        return new BuildEntry(100, 1, 755, "head", "1.0.0", "1.0.0-pr.755", packages, packages with { Id = 1 });
    }

    private sealed class DerivedMemoryCache() : MemoryCache(new MemoryCacheOptions());
}

internal static class ProxyAssertions
{
    internal static async Task EqualRecords<T>(T[] actual, T[] expected)
        where T : IEquatable<T>
    {
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < actual.Length; index++)
        {
            await Assert.That(EqualityComparer<T>.Default.Equals(actual[index], expected[index]))
                .IsTrue()
                .Because($"record at index {index} should be {expected[index]}, not {actual[index]}");
        }
    }

    internal static async Task<MemoryCache> ExactMemoryCache(IMemoryCache? cache)
    {
        await Assert.That(cache?.GetType()).IsEqualTo(typeof(MemoryCache));
        return (MemoryCache)cache!;
    }
}