using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Nuke.Common;
using Nuke.Common.ChangeLog;
using Nuke.Common.CI;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.CI.GitHubActions.Configuration;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.NerdbankGitVersioning;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.SonarScanner;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Octokit;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using YamlDotNet.Serialization;
using static DotNetCollectTasks;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tooling.ProcessTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static System.Net.WebRequestMethods;
using Project = Nuke.Common.ProjectModel.Project;

[ShutdownDotNetAfterServerBuild]
[GitHubActionsCustom(
    "CI",
    GitHubActionsImage.UbuntuLatest,
    JavaDistribution = GitHubActionJavaDistribution.Temurin,
    JavaVersion = "17",
    OnPushBranches =
    [
        "main",
        "dev",
        "releases/**",
    ],
    OnPullRequestBranches =
    [
        "main",
        "releases/**",
    ],
    
    InvokedTargets = [nameof(Pack)],
    ImportSecrets = ["GITHUB_TOKEN", "SONAR_TOKEN", nameof(NuGetApiKey)],
    WritePermissions = [GitHubActionsPermissions.Packages, GitHubActionsPermissions.Contents],
    FetchDepth = 0)]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Pack);

    [NerdbankGitVersioning(UpdateBuildNumber = true)] readonly NerdbankGitVersioning GitVersion;
    [GitRepository] readonly GitRepository Repository;
    [Solution(SuppressBuildProjectCheck = true)] readonly Solution Solution;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server).")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    static AbsolutePath SourceDirectory => RootDirectory / "src";
    static AbsolutePath PackagePropFile => SourceDirectory / "Directory.Packages.props";
    static AbsolutePath OutputDirectory => RootDirectory / "output";
    static AbsolutePath NerdbankGitVersioningDirectory => OutputDirectory / "NerdbankGitVersioning";
    static AbsolutePath TestDirectory => OutputDirectory / "tests";
    static AbsolutePath CoverageResult => OutputDirectory / "coverage";
    static AbsolutePath NugetDirectory => OutputDirectory / "nuget";
    static AbsolutePath ChangeLogFile => RootDirectory / "CHANGELOG.md";

    [Parameter(Name = "GITHUB_HEAD_REF")] readonly string GitHubHeadRef;

    [Parameter] string ArtifactsType { get; } = "*.nupkg";
    [Parameter] string ExcludedArtifactsType { get; } = "symbols.nupkg";

    [Parameter] readonly string PrNumber;
    [Parameter] readonly string PrTargetBranch;
    [Parameter] readonly string BuildBranch;
    [Parameter] readonly string BuildNumber;

    bool IsPR => !string.IsNullOrEmpty(PrNumber);

    [Parameter] string SonarHostUrl { get; } = "https://sonarcloud.io";
    [Parameter] string SonarOrganization { get; } = "frblondin-github";
    [Parameter] string SonarqubeProjectKey { get; } = "frblondin_GitDotNet";
    [Parameter, Secret] readonly string SonarToken;

    string GitHubNugetFeed => GitHubActions.Instance != null
        ? $"https://nuget.pkg.github.com/{GitHubActions.Instance.RepositoryOwner}/index.json"
        : null;
    [Parameter] string NuGetFeed { get; } = "https://api.nuget.org/v3/index.json";
    [Parameter, Secret] readonly string NuGetApiKey;

    Target Clean => _ => _
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
        });

    Target StartSonarqube => _ => _
        .DependsOn(Clean)
        .OnlyWhenStatic(() => !string.IsNullOrWhiteSpace(SonarToken))
        .WhenSkipped(DependencyBehavior.Execute)
        .AssuredAfterFailure()
        .Executes(() =>
        {
            SonarScannerTasks.SonarScannerBegin(settings =>
            {
                settings = settings
                    .SetServer(SonarHostUrl)
                    .SetOrganization(SonarOrganization)
                    .SetToken(SonarToken)
                    .SetName(SonarqubeProjectKey)
                    .SetProjectKey(SonarqubeProjectKey)
                    .SetVersion(GitVersion.AssemblyVersion)
                    .EnableExcludeTestProjects()
                    .AddAdditionalParameter("sonar.cs.roslyn.ignoreIssues", "false")
                    .AddVisualStudioCoveragePaths(CoverageResult / "coverage.xml")
                    .AddCoverageExclusions("**/*.Tests/**/*.*")
                    .AddSourceExclusions("**/*.Tests/**/*.*, *Generated*")
                    .SetVSTestReports(TestDirectory / "*.trx");

                return IsPR ?
                    settings.SetPullRequestBase(PrTargetBranch)
                            .SetPullRequestBranch(BuildBranch)
                            .SetPullRequestKey(PrNumber) :
                    settings.SetBranchName(BuildBranch);
            });
        });

    Target Restore => _ => _
        .DependsOn(StartSonarqube)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            Collect(new DotNetCollectSettings()
                .SetProcessToolPath(NuGetToolPathResolver.GetPackageExecutable("dotnet-coverage", "dotnet-coverage.dll"))
                .SetTarget(new DotNetTestSettings()
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .EnableNoRestore()
                    .SetLoggers("trx")
                    .SetResultsDirectory(TestDirectory))
                .SetConfigFile(SourceDirectory / "CoverageConfig.xml")
                .SetFormat("xml")
                .SetOutput(CoverageResult / "coverage.xml"));
        });

    Target CoverageReport => _ => _
        .DependsOn(Test)
        .AssuredAfterFailure()
        .OnlyWhenStatic(() => IsLocalBuild)
        .WhenSkipped(DependencyBehavior.Execute)
        .Executes(() =>
        {
            ReportGenerator(s => s
                .SetReports(CoverageResult / "coverage.xml")
                .SetTargetDirectory(CoverageResult));
        });

    Target EndSonarqube => _ => _
        .DependsOn(CoverageReport)
        .OnlyWhenStatic(() => !string.IsNullOrWhiteSpace(SonarToken))
        .WhenSkipped(DependencyBehavior.Execute)
        .Executes(() =>
        {
            SonarScannerTasks.SonarScannerEnd(c => c
                .SetToken(SonarToken));
        });

    Target Pack => _ => _
        .DependsOn(EndSonarqube)
        .Produces(NugetDirectory / ArtifactsType)
        .Triggers(PublishToGithub, PublishToNuGet, CreateRelease)
        .Executes(async () =>
        {
            var anyTag = GitChangeLogTasks.AnyTag();
            var modifiedFilesSinceLastTag = anyTag ? GitChangeLogTasks.ChangedFilesSinceLastTag() : [];
            var modifiedPackages = anyTag ?
                (from line in GitChangeLogTasks.GetModifiedLinesSinceLastTag(PackagePropFile)
                 let match = Regex.Match(line, @"<PackageVersion\s+[^>]*Include\s*=\s*""([^""]*)""")
                 where match.Success
                 select match.Groups[1].Value).ToList() : [];

            var serializer = SolutionSerializers.GetSerializerByMoniker(Solution.FileName);
            var slnx = await serializer.OpenAsync(Solution.Path, CancellationToken.None);
            foreach (var project in slnx.SolutionProjects
                .Where(p => CheckPackageType(p, "Dependency"))
                .Where(anyTag ? HasProjectBeenModifiedSinceLastTag : _ => true))
            {
                DotNetPack(s => s
                    .SetProject(GetProjectFilePath(project))
                    .SetConfiguration(Configuration)
                    .SetVersion(GitVersion.NuGetPackageVersion)
                    .EnableNoBuild()
                    .EnableNoRestore()
                    .SetOutputDirectory(NugetDirectory));
            }
            // Nuke doesn't support yet slnx, replace loop above with the below
            //Solution.AllProjects
            //    .Where(p => p.GetProperty<string>("PackageType") == "Dependency")
            //    .Where(anyTag ? HasProjectBeenModifiedSinceLastTag : _ => true)
            //    .ForEach(project =>
            //        DotNetPack(s => s
            //            .SetProject(project)
            //            .SetConfiguration(Configuration)
            //            .SetVersion(GitVersion.NuGetPackageVersion)
            //            .EnableNoBuild()
            //            .EnableNoRestore()
            //            .SetOutputDirectory(NugetDirectory)));

            bool HasProjectBeenModifiedSinceLastTag(SolutionProjectModel project)
            {
                var path = GetProjectFilePath(project);
                var gitPath = Path.GetDirectoryName(path).ToGitPath(RootDirectory);
                var projectContent = System.IO.File.ReadAllText(path);
                return modifiedFilesSinceLastTag.Any(f => f.StartsWith(gitPath)) ||
                       modifiedPackages.Any(package => projectContent.Contains(package));
            }

            bool CheckPackageType(SolutionProjectModel project, string packageType)
            {
                var doc = XDocument.Load(GetProjectFilePath(project));
                var packageTypeElement = doc.Descendants("PackageType").FirstOrDefault();
                return packageTypeElement != null && packageTypeElement.Value == packageType;
            }

            string GetProjectFilePath(SolutionProjectModel project) => Path.Combine(Path.GetDirectoryName(Solution.Path), project.FilePath);
        });

    Target PublishToGithub => _ => _
       .Description($"Publishing to GitHub for Development only.")
       .Requires(() => Configuration.Equals(Configuration.Release))
       .OnlyWhenStatic(() => GitHubActions.Instance != null &&
                             GitHubHeadRef != null &&
                             (GitHubHeadRef.StartsWith("dev") || GitHubHeadRef.StartsWith("feature")))
       .Executes(() =>
       {
           NugetDirectory.GlobFiles(ArtifactsType)
               .Where(x => !x.Name.EndsWith(ExcludedArtifactsType))
               .ForEach(x =>
               {
                   DotNetNuGetPush(s => s
                       .SetTargetPath(x)
                       .SetSource(GitHubNugetFeed)
                       .SetApiKey(GitHubActions.Instance.Token)
                       .EnableSkipDuplicate()
                   );
               });
       });
    
    Target PublishToNuGet => _ => _
       .Description($"Publishing to NuGet with the version.")
       .Requires(() => Configuration.Equals(Configuration.Release))
       .OnlyWhenStatic(() => GitHubActions.Instance != null &&
                             Repository.IsOnMainOrMasterBranch())
       .Executes(() =>
       {
           NugetDirectory.GlobFiles(ArtifactsType)
               .Where(x => !x.Name.EndsWith(ExcludedArtifactsType))
               .ForEach(x =>
               {
                   DotNetNuGetPush(s => s
                       .SetTargetPath(x)
                       .SetSource(NuGetFeed)
                       .SetApiKey(NuGetApiKey)
                       .EnableSkipDuplicate()
                   );
               });
       });

    Target CreateRelease => _ => _
       .Description($"Creating release for the publishable version.")
       .Requires(() => Configuration.Equals(Configuration.Release))
       .OnlyWhenStatic(() => GitHubActions.Instance != null &&
                             Repository.IsOnMainOrMasterBranch())
       .Executes(async () =>
       {
           GitHubTasks.GitHubClient = new GitHubClient(
               new ProductHeaderValue(nameof(NukeBuild)),
               new Octokit.Internal.InMemoryCredentialStore(
                   new Credentials(GitHubActions.Instance.Token)));

           var (owner, name) = (Repository.GetGitHubOwner(), Repository.GetGitHubName());

           var releaseTag = GitVersion.NuGetPackageVersion;
           var messages = GitChangeLogTasks.CommitsSinceLastTag();
           var latestChangeLog = string.Join("\n", messages.Where(IsReleaseNoteCommit).Select(TurnIntoLog));

           var newRelease = new NewRelease(releaseTag)
           {
               TargetCommitish = Repository.Commit,
               Draft = true,
               Name = $"v{releaseTag}",
               Prerelease = !(Repository.IsOnMainOrMasterBranch() || Repository.IsOnReleaseBranch()),
               Body = latestChangeLog
           };

           var createdRelease = await GitHubTasks.GitHubClient
              .Repository
              .Release.Create(owner, name, newRelease);

           NugetDirectory.GlobFiles(ArtifactsType)
              .Where(x => !x.Name.EndsWith(ExcludedArtifactsType))
              .ForEach(async x => await UploadReleaseAssetToGitHub(createdRelease, x));

           await GitHubTasks.GitHubClient
              .Repository.Release
              .Edit(owner, name, createdRelease.Id, new ReleaseUpdate { Draft = false });

           static bool IsReleaseNoteCommit(string message) =>
               !message.Contains("[skip release notes]", StringComparison.OrdinalIgnoreCase);

           static string TurnIntoLog(string message) =>
               $"- {Regex.Replace(message, @"\s*\[.*\]", string.Empty)}";
       });


    private static async Task UploadReleaseAssetToGitHub(Release release, string asset)
    {
        await using var artifactStream = System.IO.File.OpenRead(asset);
        var fileName = System.IO.Path.GetFileName(asset);
        var assetUpload = new ReleaseAssetUpload
        {
            FileName = fileName,
            ContentType = "application/octet-stream",
            RawData = artifactStream,
        };
        await GitHubTasks.GitHubClient.Repository.Release.UploadAsset(release, assetUpload);
    }
}
