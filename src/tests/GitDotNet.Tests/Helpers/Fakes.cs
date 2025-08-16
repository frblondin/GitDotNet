using System.IO.Abstractions;
using System.Reflection;
using FakeItEasy;
using FakeItEasy.Core;
using GitDotNet.Readers;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet.Tests.Helpers;

internal static class Fakes
{
    internal static RepositoryInfo CreateBareInfoProvider(string path, ConfigReaderFactory configReaderFactory, IFileSystem fileSystem) =>
        A.Fake<RepositoryInfo>(o => o
            .WithArgumentsForConstructor(() => new(path, configReaderFactory, NotImplemented<IRepositoryInfo, CurrentOperationReader>, fileSystem, new GitCliCommand()))
            .ConfigureFake(info =>
            {
                A.CallTo(() => info.Path).Returns(path);
            }));

    internal static LooseReader CreateLooseReader(string path, IFileSystem fileSystem) =>
        A.Fake<LooseReader>(o => o.WithArgumentsForConstructor(() => new(path, fileSystem, null)));

    internal static LfsReader CreateLfsReader(string path, IFileSystem fileSystem) =>
        A.Fake<LfsReader>(o => o.WithArgumentsForConstructor(() => new(path, fileSystem, null)));

    internal static IObjectResolver CreateObjectResolver(Func<HashId, Entry> entryProvider) =>
        A.Fake<IObjectResolver>(o => o.ConfigureFake(r =>
        {
            A.CallTo(r)
                .Where(call => call.Method.Name == nameof(IObjectResolver.GetAsync))
                .WithNonVoidReturnType()
                .ReturnsLazily(call => call.CreateTaskFromResult(
                    entryProvider(call.GetArgument<HashId>(0)!)));
        }));

    private static object CreateTaskFromResult(this IFakeObjectCall call, object value) =>
        typeof(Task).GetMethod(nameof(Task.FromResult))!
        .MakeGenericMethod(call.Method.ReturnType.GetGenericArguments()[0])
        .Invoke(null, [value])!;

    internal static TResult NotImplemented<TArg, TResult>(TArg _) => throw new NotImplementedException();
}
