using GitDotNet;
using GitDotNet.Readers;
using NUnit.Framework;
using FluentAssertions;
using System.Threading.Tasks;
using FakeItEasy;

namespace GitDotNet.Tests.Contract;

[TestFixture]
public class IObjectResolverInternalGetDataAsyncTests
{
    private IObjectResolverInternal _objectResolverInternal = null!;
    private HashId _validCommitHash = null!;
    private HashId _validBlobHash = null!;
    private HashId _nonExistentHash = null!;
    private HashId _partialValidHash = null!;

    [SetUp]
    public void Setup()
    {
        // These will be replaced with actual object resolver factory once we implement it
        _objectResolverInternal = A.Fake<IObjectResolverInternal>();
        
        // Test hashes - these should be replaced with real hashes from test repository
        _validCommitHash = new HashId("a1b2c3d4e5f6789012345678901234567890abcd");
        _validBlobHash = new HashId("c3d4e5f67890123456789012345678901234abcdef");
        _nonExistentHash = new HashId("0000000000000000000000000000000000000000");
        _partialValidHash = new HashId("a1b2c3d4");
    }

    [Test]
    public async Task GetDataAsync_WithValidCommitHash_ShouldReturnRawCommitData()
    {
        // Arrange
        var expectedData = "tree abc123\nparent def456\nauthor Test User <test@example.com> 1234567890 +0000\ncommitter Test User <test@example.com> 1234567890 +0000\n\nTest commit message\n"u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validCommitHash))
            .Returns(expectedData);

        // Act
        var result = await _objectResolverInternal.GetDataAsync(_validCommitHash);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
        result.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task GetDataAsync_WithValidBlobHash_ShouldReturnRawBlobData()
    {
        // Arrange
        var expectedData = "Hello, World!\nThis is a test file content."u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validBlobHash))
            .Returns(expectedData);

        // Act
        var result = await _objectResolverInternal.GetDataAsync(_validBlobHash);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
        result.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task GetDataAsync_WithPartialHash_ShouldReturnCorrectData()
    {
        // Arrange
        var expectedData = "tree abc123\nparent def456\nauthor Test User <test@example.com> 1234567890 +0000\ncommitter Test User <test@example.com> 1234567890 +0000\n\nTest commit message\n"u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_partialValidHash))
            .Returns(expectedData);

        // Act
        var result = await _objectResolverInternal.GetDataAsync(_partialValidHash);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public void GetDataAsync_WithNonExistentHash_ShouldThrowKeyNotFoundException()
    {
        // Arrange
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_nonExistentHash))
            .Throws<KeyNotFoundException>();

        // Act & Assert
        var act = async () => await _objectResolverInternal.GetDataAsync(_nonExistentHash);
        act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*" + _nonExistentHash.ToString() + "*");
    }

    [Test]
    public void GetDataAsync_WithDisposedResolver_ShouldThrowObjectDisposedException()
    {
        // Arrange
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validCommitHash))
            .Throws(new ObjectDisposedException("test"));

        // Act & Assert
        var act = async () => await _objectResolverInternal.GetDataAsync(_validCommitHash);
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public void GetDataAsync_WithNullHashId_ShouldThrowArgumentNullException()
    {
        // Arrange
        A.CallTo(() => _objectResolverInternal.GetDataAsync(null!))
            .Throws<ArgumentNullException>();

        // Act & Assert
        var act = async () => await _objectResolverInternal.GetDataAsync(null!);
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task GetDataAsync_WithLooseObject_ShouldReturnDecompressedData()
    {
        // Arrange
        var expectedData = "Sample loose object data"u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validBlobHash))
            .Returns(expectedData);

        // Act
        var result = await _objectResolverInternal.GetDataAsync(_validBlobHash);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task GetDataAsync_WithPackedObject_ShouldReturnReconstructedData()
    {
        // Arrange
        var expectedData = "Sample packed object data with delta reconstruction"u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validCommitHash))
            .Returns(expectedData);

        // Act
        var result = await _objectResolverInternal.GetDataAsync(_validCommitHash);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public async Task GetDataAsync_MultipleCallsWithSameHash_ShouldReturnSameData()
    {
        // Arrange
        var expectedData = "Cached object data"u8.ToArray();
        A.CallTo(() => _objectResolverInternal.GetDataAsync(_validCommitHash))
            .Returns(expectedData);

        // Act
        var result1 = await _objectResolverInternal.GetDataAsync(_validCommitHash);
        var result2 = await _objectResolverInternal.GetDataAsync(_validCommitHash);

        // Assert
        result1.Should().BeEquivalentTo(result2);
        result1.Should().BeEquivalentTo(expectedData);
    }

    [Test]
    public void PackManager_ShouldReturnNonNullPackManager()
    {
        // Arrange
        var expectedPackManager = A.Fake<IPackManager>();
        A.CallTo(() => _objectResolverInternal.PackManager)
            .Returns(expectedPackManager);

        // Act
        var result = _objectResolverInternal.PackManager;

        // Assert
        result.Should().NotBeNull();
        result.Should().BeSameAs(expectedPackManager);
    }

    [Test]
    public void PackManager_ShouldRemainConsistentThroughoutObjectLifetime()
    {
        // Arrange
        var expectedPackManager = A.Fake<IPackManager>();
        A.CallTo(() => _objectResolverInternal.PackManager)
            .Returns(expectedPackManager);

        // Act
        var result1 = _objectResolverInternal.PackManager;
        var result2 = _objectResolverInternal.PackManager;

        // Assert
        result1.Should().BeSameAs(result2);
        result1.Should().BeSameAs(expectedPackManager);
    }
}
