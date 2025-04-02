using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using GitDotNet.Readers;
using GitDotNet.Tests.Properties;
using GitDotNet.Tools;
using Microsoft.Extensions.DependencyInjection;
using static GitDotNet.Tests.Helpers.Fakes;

namespace GitDotNet.Tests.Helpers;
internal static class DependencyInjectionProvider
{
    internal static GitConnectionProvider CreateProvider() => CreateProvider(out var _);

    internal static GitConnectionProvider CreateProvider(out ServiceProvider provider)
    {
        provider = CreateServiceProvider();
        return provider.GetRequiredService<GitConnectionProvider>();
    }

    internal static ServiceProvider CreateServiceProvider(IFileSystem? fileSystem = null,
        ConfigReader? configReader = null,
        IObjectResolver? objectResolver = null,
        CommitGraphReader? commitGraphReader = null,
        FileOffsetStreamReaderFactory? offsetStreamReaderFactory = null,
        Func<IServiceProvider, RepositoryInfoFactory>? repositoryInfoFactory = null)
    {
        var collection = new ServiceCollection()
                    .AddMemoryCache()
                    .AddGitDotNet();

        if (fileSystem != null)
            collection.AddSingleton(fileSystem);
        if (configReader != null)
            collection.AddSingleton<ConfigReaderFactory>((_) => configReader);
        if (objectResolver != null)
            collection.AddSingleton<ObjectResolverFactory>((_, _) => objectResolver);
        if (commitGraphReader != null)
            collection.AddSingleton<CommitGraphReaderFactory>((_, _) => commitGraphReader);
        if (offsetStreamReaderFactory != null)
            collection.AddSingleton(offsetStreamReaderFactory);
        if (repositoryInfoFactory != null)
            collection.AddSingleton(repositoryInfoFactory);

        return collection.BuildServiceProvider();
    }

    internal static GitConnectionProvider CreateProviderUsingFakeFileSystem(ref MockFileSystem? fileSystem,
        ConfigReader? configReader = null,
        IObjectResolver? objectResolver = null,
        CommitGraphReader? commitGraphReader = null)
    {
        fileSystem ??= new MockFileSystem().AddZipContent(Resource.CompleteRepository);
        var captured = fileSystem;
        var serviceProvider = CreateServiceProvider(fileSystem, configReader, objectResolver, commitGraphReader,
            captured.CreateOffsetReader,
            sp => path => CreateBareInfoProvider(path, sp.GetRequiredService<ConfigReaderFactory>(), captured));

        return serviceProvider.GetRequiredService<GitConnectionProvider>();
    }
}
