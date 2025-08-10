using GitDotNet;
using GitDotNet.Readers;
using GitDotNet.Tools;
using GitDotNet.Writers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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
        .AddScoped<RepositoryInfoFactory>(sp => path =>
            new(path,
                sp.GetRequiredService<ConfigReaderFactory>(),
                sp.GetRequiredService<CurrentOperationReaderFactory>(),
                sp.GetRequiredService<IFileSystem>(),
                sp.GetRequiredService<GitCliCommand>()))
        .AddScoped<GitConnectionProvider>(sp => path =>
            new(path,
                sp.GetRequiredService<RepositoryInfoFactory>(),
                sp.GetRequiredService<ObjectResolverFactory>(),
                sp.GetRequiredService<BranchRefReaderFactory>(),
                sp.GetRequiredService<IndexFactory>(),
                sp.GetRequiredService<ITreeComparer>(),
                sp.GetRequiredService<TransformationComposerFactory>(),
                sp.GetRequiredService<IFileSystem>()))
        .AddScoped<ConfigReaderFactory>(sp => path =>
            new(path, sp.GetRequiredService<IFileSystem>()));

    private static IServiceCollection AddReaders(this IServiceCollection services) => services
        .AddSingleton<ITreeComparer, TreeComparer>()
        .AddScoped<FileOffsetStreamReaderFactory>(sp => path =>
            new FileOffsetStreamReader(path,
                                       sp.GetRequiredService<IFileSystem>().FileInfo.New(path).Length))
        .AddScoped<CurrentOperationReaderFactory>(sp => info => new(info))
        .AddScoped<PackManagerFactory>(sp => path =>
            new PackManager(path,
                            sp.GetRequiredService<IFileSystem>(),
                            sp.GetRequiredService<PackReaderFactory>()))
        .AddScoped<IndexReaderFactory>(sp => (path, entryProvider) =>
            new(path,
                entryProvider,
                sp.GetRequiredService<IFileSystem>()))
        .AddScoped<IndexFactory>(sp => (repositoryPath, entryProvider) =>
            new(repositoryPath, entryProvider, sp.GetRequiredService<IndexReaderFactory>(), sp.GetRequiredService<IFileSystem>()))
        .AddScoped<ObjectResolverFactory>(sp => (path, useReadCommitGraph) =>
            new ObjectResolver(
                path, useReadCommitGraph,
                sp.GetRequiredService<IOptions<GitConnection.Options>>(),
                sp.GetRequiredService<PackManagerFactory>(),
                sp.GetRequiredService<LooseReaderFactory>(),
                sp.GetRequiredService<LfsReaderFactory>(),
                sp.GetRequiredService<CommitGraphReaderFactory>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<IFileSystem>()))
        .AddScoped<BranchRefReaderFactory>(sp => (connection) =>
            new(connection,
                sp.GetRequiredService<IFileSystem>()))
       .AddScoped<LooseReaderFactory>(sp => path =>
            new(path,
                sp.GetRequiredService<IFileSystem>()))
        .AddScoped<LfsReaderFactory>(sp => path =>
            new(path,
                sp.GetRequiredService<IFileSystem>()))
        .AddScoped<CommitGraphReaderFactory>(sp => (path, objectResolver) =>
            CommitGraphReader.Load(path,
                                   objectResolver,
                                   sp.GetRequiredService<IFileSystem>(),
                                   sp.GetRequiredService<FileOffsetStreamReaderFactory>()))
        .AddScoped<PackReaderFactory>(sp => (string path) =>
            new(sp.GetRequiredService<FileOffsetStreamReaderFactory>().Invoke(path),
                sp.GetRequiredService<PackIndexFactory>()))
        .AddScoped<PackIndexFactory>(sp => async path =>
            await PackIndexReader.LoadAsync(path, sp.GetRequiredService<IFileSystem>()))
        .AddScoped<IPackManager, PackManager>();

    private static IServiceCollection AddWriters(this IServiceCollection services) => services
        .AddScoped<FastInsertWriterFactory>(sp => stream =>
            new(stream))
        .AddScoped<TransformationComposerFactory>(sp => repositoryPath =>
            new TransformationComposer(repositoryPath,
                                        sp.GetRequiredService<FastInsertWriterFactory>(),
                                        sp.GetRequiredService<IFileSystem>()));
}