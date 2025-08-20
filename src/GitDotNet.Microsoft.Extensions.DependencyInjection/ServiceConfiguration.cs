using GitDotNet;
using GitDotNet.Readers;
using GitDotNet.Tools;
using GitDotNet.Writers;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>A set of methods for instances of <see cref="IServiceCollection"/>.</summary>
public static class ServiceConfiguration
{
    /// <summary>Adds access to GitObjectDb repositories.</summary>
    /// <param name="source">The source.</param>
    /// <param name="configure">A delegate to configure the <see cref="GitConnection.Options"/>.</param>
    /// <returns>The source <see cref="IServiceCollection"/>.</returns>
    public static IServiceCollection AddGitDotNet(this IServiceCollection source,
        Action<GitConnection.Options>? configure = null)
    {
        if (configure != null)
        {
            source.Configure(configure);
        }

        return source
            // Dependencies
            .AddSingleton<IFileSystem, FileSystem>()
            // Options
            .AddOptions()
            .AddSingleton(sp => { var options = new GitConnection.Options(); configure?.Invoke(options); return options; })
            .AddMain()
            .AddReaders()
            .AddWriters();
    }

    private static IServiceCollection AddMain(this IServiceCollection services) => services
        .AddScoped<GitCliCommand>()
        .AddAutoFactory<RepositoryInfoFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<GitConnectionProvider>(ServiceLifetime.Scoped)
        .AddAutoFactory<ConfigReaderFactory>(ServiceLifetime.Scoped);

    private static IServiceCollection AddReaders(this IServiceCollection services) => services
        .AddSingleton<ITreeComparer, TreeComparer>()
        .AddAutoFactory<FileOffsetStreamReaderFactory, FileOffsetStreamReader>(ServiceLifetime.Scoped)
        .AddAutoFactory<CurrentOperationReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<PackManagerFactory, PackManager>(ServiceLifetime.Scoped)
        .AddAutoFactory<IndexReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<IndexFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<ObjectResolverFactory, ObjectResolver>(ServiceLifetime.Scoped)
        .AddAutoFactory<BranchRefReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<LooseReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<LfsReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<CommitGraphReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<PackReaderFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<StashRefReaderFactory>(ServiceLifetime.Scoped)
        .AddScoped<PackIndexFactory>(sp => path => 
            PackIndexReader.LoadAsync(
                path,
                sp.GetRequiredService<IFileSystem>(),
                sp.GetService<ILogger<PackIndexReader>>()))
        .AddScoped<IPackManager, PackManager>();

    private static IServiceCollection AddWriters(this IServiceCollection services) => services
        .AddAutoFactory<FastInsertWriterFactory>(ServiceLifetime.Scoped)
        .AddAutoFactory<TransformationComposerFactory, TransformationComposer>(ServiceLifetime.Scoped);
}