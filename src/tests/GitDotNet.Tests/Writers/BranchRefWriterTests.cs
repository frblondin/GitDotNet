using System.Collections.Immutable;
using System.IO.Abstractions.TestingHelpers;
using FluentAssertions;
using FluentAssertions.Execution;
using GitDotNet.Writers;
using GitDotNet.Readers;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using FakeItEasy;

namespace GitDotNet.Tests.Writers;

public class BranchRefWriterTests
{
    private MockFileSystem _fileSystem;
    private IRepositoryInfo _repositoryInfo;
    private IBranchRefReader _branchRefReader;
    private BranchRefWriter _branchRefWriter;
    private string _gitPath;

    [SetUp]
    public void Setup()
    {
        _fileSystem = new MockFileSystem();
        _gitPath = "/test-repo/.git";
        
        // Create the repository structure
        _fileSystem.Directory.CreateDirectory(_gitPath);
        _fileSystem.Directory.CreateDirectory($"{_gitPath}/refs");
        _fileSystem.Directory.CreateDirectory($"{_gitPath}/refs/heads");
        _fileSystem.Directory.CreateDirectory($"{_gitPath}/refs/remotes");
        
        // Mock dependencies
        _repositoryInfo = A.Fake<IRepositoryInfo>();
        _branchRefReader = A.Fake<IBranchRefReader>();
        
        A.CallTo(() => _repositoryInfo.Path).Returns(_gitPath);
        
        _branchRefWriter = new BranchRefWriter(_repositoryInfo, _branchRefReader, _fileSystem, NullLogger<BranchRefWriter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        // BranchRefWriter no longer implements IDisposable
        // No cleanup needed as it doesn't hold any resources
    }

    /// <summary>
    /// Creates a Branch instance for testing purposes using a minimal null implementation of IGitConnection.
    /// </summary>
    private static Branch CreateTestBranch(string canonicalName, HashId commitHash)
    {
        var nullConnection = new NullGitConnection();
        return new Branch(canonicalName, nullConnection, () => commitHash);
    }

    [Test]
    public void CreateOrUpdateLocalBranch_WithValidInput_CreatesRefFile()
    {
        // Arrange
        var branchName = "feature-branch";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");

        // Act
        _branchRefWriter.CreateOrUpdateLocalBranch(branchName, commitHash);

        // Assert
        using (new AssertionScope())
        {
            var expectedPath = $"{_gitPath}/refs/heads/{branchName}";
            _fileSystem.File.Exists(expectedPath).Should().BeTrue("Branch ref file should be created");
            var content = _fileSystem.File.ReadAllText(expectedPath).Trim();
            content.Should().Be(commitHash.ToString(), "Ref file should contain the commit hash");
        }
    }

    [Test]
    public void CreateOrUpdateLocalBranch_WithNestedBranchName_CreatesDirectoryStructure()
    {
        // Arrange
        var branchName = "feature/sub-feature/my-branch";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");

        // Act
        _branchRefWriter.CreateOrUpdateLocalBranch(branchName, commitHash);

        // Assert
        using (new AssertionScope())
        {
            var expectedPath = $"{_gitPath}/refs/heads/feature/sub-feature/my-branch";
            _fileSystem.File.Exists(expectedPath).Should().BeTrue("Nested branch ref file should be created");
            var content = _fileSystem.File.ReadAllText(expectedPath).Trim();
            content.Should().Be(commitHash.ToString(), "Ref file should contain the commit hash");
        }
    }

    [Test]
    public void CreateOrUpdateLocalBranch_WithExistingBranchAndOverwriteDisabled_ThrowsException()
    {
        // Arrange
        var branchName = "existing-branch";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");
        var canonicalName = $"refs/heads/{branchName}";
        
        // Setup existing branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, commitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Act & Assert
        FluentActions.Invoking(() => _branchRefWriter.CreateOrUpdateLocalBranch(branchName, commitHash, allowOverwrite: false))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"Branch 'refs/heads/{branchName}' already exists. Use allowOverwrite=true to overwrite it.");
    }

    [Test]
    public void CreateOrUpdateLocalBranch_WithExistingBranchAndOverwriteEnabled_UpdatesRefFile()
    {
        // Arrange
        var branchName = "existing-branch";
        var oldCommitHash = new HashId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var newCommitHash = new HashId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var canonicalName = $"refs/heads/{branchName}";
        
        // Setup existing branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, oldCommitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create existing ref file
        var refPath = $"{_gitPath}/refs/heads/{branchName}";
        _fileSystem.AddFile(refPath, new MockFileData(oldCommitHash.ToString()));

        // Act
        _branchRefWriter.CreateOrUpdateLocalBranch(branchName, newCommitHash, allowOverwrite: true);

        // Assert
        using (new AssertionScope())
        {
            _fileSystem.File.Exists(refPath).Should().BeTrue("Ref file should still exist");
            var content = _fileSystem.File.ReadAllText(refPath).Trim();
            content.Should().Be(newCommitHash.ToString(), "Ref file should contain the new commit hash");
        }
    }

    [Test]
    public void CreateOrUpdateRemoteBranch_WithValidInput_CreatesRefFile()
    {
        // Arrange
        var remoteName = "origin";
        var branchName = "main";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");

        // Act
        _branchRefWriter.CreateOrUpdateRemoteBranch(remoteName, branchName, commitHash);

        // Assert
        using (new AssertionScope())
        {
            var expectedPath = $"{_gitPath}/refs/remotes/{remoteName}/{branchName}";
            _fileSystem.File.Exists(expectedPath).Should().BeTrue("Remote branch ref file should be created");
            var content = _fileSystem.File.ReadAllText(expectedPath).Trim();
            content.Should().Be(commitHash.ToString(), "Ref file should contain the commit hash");
        }
    }

    [Test]
    public void DeleteLocalBranch_WithExistingBranch_DeletesRefFile()
    {
        // Arrange
        var branchName = "feature-to-delete";
        var canonicalName = $"refs/heads/{branchName}";
        var refPath = $"{_gitPath}/refs/heads/{branchName}";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");
        
        // Setup existing branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, commitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create the ref file
        _fileSystem.AddFile(refPath, new MockFileData(commitHash.ToString()));

        // Act
        _branchRefWriter.DeleteLocalBranch(branchName, force: true);

        // Assert
        _fileSystem.File.Exists(refPath).Should().BeFalse("Ref file should be deleted");
    }

    [Test]
    public void DeleteLocalBranch_WithNonExistentBranch_ThrowsException()
    {
        // Arrange
        var branchName = "non-existent-branch";
        var canonicalName = $"refs/heads/{branchName}";
        
        // Setup non-existent branch
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(ImmutableDictionary<string, Branch>.Empty);

        // Act & Assert
        FluentActions.Invoking(() => _branchRefWriter.DeleteLocalBranch(branchName))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"Branch 'refs/heads/{branchName}' does not exist.");
    }

    [Test]
    public void DeleteRemoteBranch_WithExistingBranch_DeletesRefFile()
    {
        // Arrange
        var remoteName = "origin";
        var branchName = "feature-to-delete";
        var canonicalName = $"refs/remotes/{remoteName}/{branchName}";
        var refPath = $"{_gitPath}/refs/remotes/{remoteName}/{branchName}";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");
        
        // Setup existing remote branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, commitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create the ref file
        _fileSystem.Directory.CreateDirectory($"{_gitPath}/refs/remotes/{remoteName}");
        _fileSystem.AddFile(refPath, new MockFileData(commitHash.ToString()));

        // Act
        _branchRefWriter.DeleteRemoteBranch(remoteName, branchName);

        // Assert
        _fileSystem.File.Exists(refPath).Should().BeFalse("Remote ref file should be deleted");
    }

    [Test]
    public void CreateOrUpdateLocalBranch_RemovesFromPackedRefsIfExists()
    {
        // Arrange
        var branchName = "packed-branch";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");
        var canonicalName = $"refs/heads/{branchName}";
        
        // Create packed-refs file with the branch
        var packedRefsPath = $"{_gitPath}/packed-refs";
        var packedRefsContent = @"# pack-refs with: peeled fully-peeled sorted 
# some comment
1111111111111111111111111111111111111111 refs/heads/other-branch
2222222222222222222222222222222222222222 refs/heads/packed-branch
3333333333333333333333333333333333333333 refs/heads/another-branch";
        
        _fileSystem.AddFile(packedRefsPath, new MockFileData(packedRefsContent));

        // Act
        _branchRefWriter.CreateOrUpdateLocalBranch(branchName, commitHash);

        // Assert
        using (new AssertionScope())
        {
            // Check that ref file was created
            var refPath = $"{_gitPath}/refs/heads/{branchName}";
            _fileSystem.File.Exists(refPath).Should().BeTrue("Ref file should be created");
            
            // Check that branch was removed from packed-refs
            var updatedPackedRefs = _fileSystem.File.ReadAllText(packedRefsPath);
            updatedPackedRefs.Should().NotContain("refs/heads/packed-branch", "Branch should be removed from packed-refs");
            updatedPackedRefs.Should().Contain("refs/heads/other-branch", "Other branches should remain");
            updatedPackedRefs.Should().Contain("refs/heads/another-branch", "Other branches should remain");
        }
    }

    [Test]
    public void DeleteLocalBranch_RemovesFromPackedRefsIfExists()
    {
        // Arrange
        var branchName = "packed-branch";
        var canonicalName = $"refs/heads/{branchName}";
        var commitHash = new HashId("2222222222222222222222222222222222222222");
        
        // Setup existing branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, commitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create packed-refs file with the branch
        var packedRefsPath = $"{_gitPath}/packed-refs";
        var packedRefsContent = @"# pack-refs with: peeled fully-peeled sorted 
1111111111111111111111111111111111111111 refs/heads/other-branch
2222222222222222222222222222222222222222 refs/heads/packed-branch
3333333333333333333333333333333333333333 refs/heads/another-branch";
        
        _fileSystem.AddFile(packedRefsPath, new MockFileData(packedRefsContent));

        // Act
        _branchRefWriter.DeleteLocalBranch(branchName, force: true);

        // Assert
        using (new AssertionScope())
        {
            // Check that branch was removed from packed-refs
            var updatedPackedRefs = _fileSystem.File.ReadAllText(packedRefsPath);
            updatedPackedRefs.Should().NotContain("refs/heads/packed-branch", "Branch should be removed from packed-refs");
            updatedPackedRefs.Should().Contain("refs/heads/other-branch", "Other branches should remain");
            updatedPackedRefs.Should().Contain("refs/heads/another-branch", "Other branches should remain");
        }
    }

    [Test]
    public void PackedRefsManagement_WithNoPackedRefsFile_DoesNotFail()
    {
        // Arrange
        var branchName = "new-branch";
        var commitHash = new HashId("1234567890abcdef1234567890abcdef12345678");

        // Act & Assert (should not throw)
        FluentActions.Invoking(() => _branchRefWriter.CreateOrUpdateLocalBranch(branchName, commitHash))
            .Should().NotThrow("Should handle missing packed-refs file gracefully");

        // Verify the ref file was still created
        var refPath = $"{_gitPath}/refs/heads/{branchName}";
        _fileSystem.File.Exists(refPath).Should().BeTrue("Ref file should be created even without packed-refs");
    }

    [Test]
    public void PackedRefsManagement_PreservesCommentsAndEmptyLines()
    {
        // Arrange
        var branchName = "branch-to-remove";
        var canonicalName = $"refs/heads/{branchName}";
        var commitHash = new HashId("2222222222222222222222222222222222222222");
        
        // Setup existing branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, commitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create packed-refs file with comments, empty lines, and peeled refs
        var packedRefsPath = $"{_gitPath}/packed-refs";
        var packedRefsContent = @"# pack-refs with: peeled fully-peeled sorted 

# This is a comment
1111111111111111111111111111111111111111 refs/heads/other-branch
^aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
2222222222222222222222222222222222222222 refs/heads/branch-to-remove

# Another comment
3333333333333333333333333333333333333333 refs/heads/another-branch";
        
        _fileSystem.AddFile(packedRefsPath, new MockFileData(packedRefsContent));

        // Act
        _branchRefWriter.DeleteLocalBranch(branchName, force: true);

        // Assert
        using (new AssertionScope())
        {
            var updatedPackedRefs = _fileSystem.File.ReadAllText(packedRefsPath);
            updatedPackedRefs.Should().NotContain("refs/heads/branch-to-remove", "Target branch should be removed");
            updatedPackedRefs.Should().Contain("# pack-refs with: peeled fully-peeled sorted", "Header comment should be preserved");
            updatedPackedRefs.Should().Contain("# This is a comment", "Comments should be preserved");
            updatedPackedRefs.Should().Contain("# Another comment", "Comments should be preserved");
            updatedPackedRefs.Should().Contain("refs/heads/other-branch", "Other branches should remain");
            updatedPackedRefs.Should().Contain("refs/heads/another-branch", "Other branches should remain");
            updatedPackedRefs.Should().Contain("^aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Peeled refs should be preserved");
        }
    }

    [Test]
    public void CreateOrUpdateRemoteBranch_DefaultAllowOverwrite_IsTrue()
    {
        // Arrange
        var remoteName = "origin";
        var branchName = "main";
        var oldCommitHash = new HashId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var newCommitHash = new HashId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var canonicalName = $"refs/remotes/{remoteName}/{branchName}";
        
        // Setup existing remote branch using real constructor
        var existingBranch = CreateTestBranch(canonicalName, oldCommitHash);
        var branches = new Dictionary<string, Branch> { { canonicalName, existingBranch } };
        A.CallTo(() => _branchRefReader.GetBranches()).Returns(branches.ToImmutableDictionary());

        // Create existing remote ref file
        var refPath = $"{_gitPath}/refs/remotes/{remoteName}/{branchName}";
        _fileSystem.Directory.CreateDirectory($"{_gitPath}/refs/remotes/{remoteName}");
        _fileSystem.AddFile(refPath, new MockFileData(oldCommitHash.ToString()));

        // Act - Note: not explicitly setting allowOverwrite, should default to true for remote branches
        _branchRefWriter.CreateOrUpdateRemoteBranch(remoteName, branchName, newCommitHash);

        // Assert
        using (new AssertionScope())
        {
            _fileSystem.File.Exists(refPath).Should().BeTrue("Remote ref file should still exist");
            var content = _fileSystem.File.ReadAllText(refPath).Trim();
            content.Should().Be(newCommitHash.ToString(), "Remote ref file should contain the new commit hash");
        }
    }

    /// <summary>
    /// A minimal null implementation of IGitConnection for testing purposes.
    /// This implementation provides only the minimal required functionality for Branch instantiation.
    /// </summary>
    private class NullGitConnection : IGitConnection
    {
        private readonly IRepositoryInfo _repositoryInfo = A.Fake<IRepositoryInfo>();

        public Branch.List Branches => throw new NotImplementedException();
        public Branch Head => throw new NotImplementedException();
        public Index Index => throw new NotImplementedException();
        public RepositoryInfo Info => throw new NotImplementedException();
        public IObjectResolver Objects => throw new NotImplementedException();
        public Remote.List Remotes => throw new NotImplementedException();

        public static void Clone(string path, string url, CloneOptions? options = null) => throw new NotImplementedException();
        public static void Create(string path, bool isBare = false) => throw new NotImplementedException();
        public static bool IsValid(string path) => throw new NotImplementedException();

        public Task<CommitEntry> CommitAsync(string message, Signature? author = null, Signature? committer = null, CommitOptions? options = null) => throw new NotImplementedException();
        public Task<CommitEntry> CommitAsync(string branchName, Action<ITransformationComposer> transformations, CommitEntry commit, CommitOptions? options = null) => throw new NotImplementedException();
        public Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, Task> transformations, CommitEntry commit, CommitOptions? options = null) => throw new NotImplementedException();
        public Task<IList<Change>> CompareAsync(CommitEntry? old, CommitEntry @new) => throw new NotImplementedException();
        public Task<IList<Change>> CompareAsync(string? old, string @new) => throw new NotImplementedException();
        public Task<IList<Change>> CompareAsync(TreeEntry? old, TreeEntry @new) => throw new NotImplementedException();
        public CommitEntry CreateCommit(string message, IList<CommitEntry> parents, Signature? author = null, Signature? committer = null) => throw new NotImplementedException();
        public void Dispose() { }
        public Task<CommitEntry> GetCommittishAsync(string committish) => throw new NotImplementedException();
        public IAsyncEnumerable<LogEntry> GetLogAsync(string committish, LogOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CommitEntry?> GetMergeBaseAsync(string committish1, string committish2) => throw new NotImplementedException();
        public Task<IReadOnlyList<Stash>> GetStashesAsync() => throw new NotImplementedException();
    }
}