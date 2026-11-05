using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NuGet.Packaging.Signing;

namespace Keboo._127Cache.Tests;

internal sealed class PackageRewriteFixture : IDisposable
{
    public const string CorePropertiesPath = "package/services/metadata/core-properties/nuget.psmdcp";
    public const string WidgetDependencies = """
        <dependencies>
          <group targetFramework="net8.0">
            <dependency id="acme.core" version="[1.0.0,2.0.0)" include="Compile" exclude="Runtime" />
            <dependency id="External.Alpha" version="[3.1.0,4.0.0)" include="All" exclude="Build" />
          </group>
          <group targetFramework="net9.0">
            <dependency id="ACME.CORE" version="(,2.0.0]" include="All" exclude="Analyzers" />
            <dependency id="External.Beta" version="(,5.0.0]" />
          </group>
        </dependencies>
        """;

    public string DirectoryPath { get; } = Path.Combine(Directory.GetCurrentDirectory(), "issue16-fixture-" + Guid.NewGuid().ToString("N"));

    public PackageRewriteFixture() => Directory.CreateDirectory(DirectoryPath);

    public string CreatePackage(string id, string version, string dependencies = "", string? coreIdentifier = null,
        bool additionalCoreProperties = false, bool omitCoreVersion = false)
    {
        var path = Path.Combine(DirectoryPath, id + ".nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, id + ".nuspec", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id><version>{version}</version><authors>Example Author</authors>
                <description>Golden package fixture</description>
                {dependencies}
              </metadata>
            </package>
            """);
        WriteEntry(archive, CorePropertiesPath, CoreProperties(coreIdentifier ?? id, omitCoreVersion ? null : version));
        if (additionalCoreProperties)
            WriteEntry(archive, "package/services/metadata/core-properties/extra.psmdcp", CoreProperties(id, version));
        WriteEntry(archive, "_rels/.rels", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/{id}.nuspec" Id="manifest" />
              <Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/{CorePropertiesPath}" Id="properties" />
            </Relationships>
            """);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
              <Default Extension="psmdcp" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
              <Default Extension="nuspec" ContentType="application/octet" />
              <Default Extension="txt" ContentType="application/octet" />
              <Default Extension="p7s" ContentType="application/octet" />
            </Types>
            """);
        WriteEntry(archive, "runtimes/linux-x64/native/tool.txt", "payload\0with unicode: \u2603");
        WriteEntry(archive, "docs/.signature.p7s", "ordinary nested payload");
        WriteEntry(archive, ".SIGNATURE.P7S", "ordinary payload with a different case");
        return path;
    }

    public async Task<string> SignAsync(string packagePath)
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=127Cache test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.3") };
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: true));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var request = new AuthorSignPackageRequest(certificate, NuGet.Common.HashAlgorithmName.SHA256)
        {
            AllowUntrustedRoot = true
        };
        var signed = Path.Combine(DirectoryPath, "signed-" + Path.GetFileName(packagePath));
        using (var options = SigningOptions.CreateFromFilePaths(packagePath, signed, overwrite: false,
            new X509SignatureProvider(null!), NuGet.Common.NullLogger.Instance))
        {
            await SigningUtility.SignAsync(options, request, CancellationToken.None);
        }
        return signed;
    }

    public async Task<FixtureGitHub> CreateGitHubAsync(bool signCore = false)
    {
        var core = CreatePackage("Acme.Core", "1.2.0-beta.3+sha");
        if (signCore) core = await SignAsync(core);
        var widget = CreatePackage("Acme.Widget", "2.0.0-rc.1", WidgetDependencies);
        var empty = CreatePackage("Acme.Empty", "1.2.3.4+build");
        var packages = Path.Combine(DirectoryPath, "packages.zip");
        using (var archive = ZipFile.Open(packages, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(core, "Acme.Core.nupkg");
            archive.CreateEntryFromFile(widget, "Acme.Widget.nupkg");
            archive.CreateEntryFromFile(empty, "Acme.Empty.nupkg");
        }
        var version = Path.Combine(DirectoryPath, "version.zip");
        using (var archive = ZipFile.Open(version, ZipArchiveMode.Create))
            WriteEntry(archive, "version.txt", "1.2.0-beta.3+sha");
        return new FixtureGitHub(packages, version);
    }

    public static XDocument ReadXml(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    private static string CoreProperties(string id, string? version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coreProperties xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                        xmlns:dc="http://purl.org/dc/elements/1.1/">
          <dc:creator>Example Author</dc:creator><dc:identifier>{id}</dc:identifier>{(version is null ? "" : $"<version>{version}</version>")}
        </coreProperties>
        """;

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path);
        entry.LastWriteTime = new DateTimeOffset(2020, 5, 6, 12, 34, 56, TimeSpan.Zero);
        entry.ExternalAttributes = 0x1ED << 16;
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(value));
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class FixtureGitHub(string packagesPath, string versionPath) : IGitHubClient
{
    public Artifact Packages { get; } = CreateArtifact(200, "packages", packagesPath);
    public Artifact Version { get; } = CreateArtifact(201, "build-version", versionPath);
    public bool FailDownloads { get; set; }
    public int Downloads { get; private set; }
    public WorkflowRun[] Runs { get; set; } = [new(100, 1, "head", "completed", "success", "pull_request", [new(755)])];

    public Task<IReadOnlyList<PullRequestInfo>> GetOpenPullRequestsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PullRequestInfo>>([new(755)]);

    public Task<IReadOnlyList<WorkflowRun>> GetRunsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowRun>>(Runs);

    public Task<IReadOnlyList<Artifact>> GetArtifactsAsync(long runId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Artifact>>([Packages, Version]);

    public Task DownloadAsync(Artifact artifact, string destination, CancellationToken cancellationToken)
    {
        if (FailDownloads) throw new IOException("Downloads are disabled in this fixture.");
        Downloads++;
        File.Copy(artifact.Id == Packages.Id ? packagesPath : versionPath, destination);
        return Task.CompletedTask;
    }

    public void SeedRawCache(string cacheDirectory)
    {
        var directory = Path.Combine(cacheDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        File.Copy(packagesPath, Path.Combine(directory, $"{Packages.Id}.zip"));
        File.Copy(versionPath, Path.Combine(directory, $"{Version.Id}.zip"));
    }

    private static Artifact CreateArtifact(long id, string name, string path) =>
        new(id, name, false, "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
}

internal sealed class CollectingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Enqueue((logLevel, formatter(state, exception)));
}
