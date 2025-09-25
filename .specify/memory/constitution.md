<!--
Sync Impact Report:
Version change: 1.0.0 → 1.1.0
Modified principles: None
Added sections: VI. Architectural Organization - new principle for codebase structure
Removed sections: None
Templates requiring updates: ✅ No template updates needed
Follow-up TODOs: None - all placeholders filled with concrete values
-->

# GitDotNet Constitution

## Core Principles

### I. Git Compatibility (NON-NEGOTIABLE)
GitDotNet MUST remain fully compatible with Git requirements so that repositories modified by GitDotNet stay interoperable with any other Git tool. This includes:
- Strict adherence to Git object format specifications (commits, trees, blobs, tags)
- Proper handling of Git index format and reference storage
- Compliance with Git configuration and hooks mechanisms
- Preservation of Git repository integrity across all operations

**Rationale**: Interoperability is fundamental to Git's ecosystem. Breaking compatibility would isolate GitDotNet users from the broader Git toolchain.

### II. Performance Parity
Performance MUST be equivalent to or better than native Git commands. This principle drives:
- Memory-mapped file usage for efficient object access
- Lazy loading and streaming for minimal memory footprint
- Asynchronous operations for non-blocking I/O
- Pack file optimization for large repository operations
- .NET 9 preference for zlib-ng performance benefits

**Rationale**: Performance is a key differentiator. Users won't adopt a slower alternative to native Git commands.

### III. .NET Native Implementation
GitDotNet MUST eliminate dependencies on Git commands and become fully .NET native:
- All Git operations implemented in managed code
- No process spawning for Git command execution
- Direct file system and memory operations
- Native .NET compression and hashing algorithms
- Pure .NET serialization and deserialization

**Rationale**: Native implementation provides better performance, reduces dependencies, and enables better integration with .NET applications.

### IV. Memory Efficiency
Memory usage MUST be optimized for scalability:
- IMemoryCache integration with configurable size limits
- Object pooling for frequently allocated objects
- Streaming operations for large data handling
- Garbage collection pressure minimization
- Memory-mapped files for large repository data

**Rationale**: Git repositories can be massive. Efficient memory usage ensures GitDotNet scales to enterprise-level repositories.

### V. API Design Excellence
Public APIs MUST follow .NET conventions and best practices:
- Async/await patterns for I/O operations
- IDisposable for resource management
- Generic types for type safety
- Extension methods for DI integration
- Comprehensive XML documentation

**Rationale**: GitDotNet is a library first. Excellent API design ensures developer productivity and adoption.

### VI. Architectural Organization
Code organization MUST follow established patterns for maintainability and clarity:
- **Data namespace**: Contains immutable data models and Git object representations (CommitEntry, TreeEntry, BlobEntry, etc.)
- **Readers namespace**: Contains classes responsible for reading Git repository data (ConfigReader, PackReader, IndexReader, etc.)
- **Writers namespace**: Contains classes responsible for writing Git repository data (PackWriter, LooseWriter, BranchRefWriter, etc.)
- **Tools namespace**: Contains utility classes and cross-cutting concerns (PooledMemoryStream, HashTools, GitPatchCreator, etc.)
- **IFileSystem abstraction**: All file system operations MUST use System.IO.Abstractions.IFileSystem for testability and platform independence
- **Factory pattern**: Component creation MUST use factory delegates for dependency injection and lifecycle management
- **Async suffix**: All asynchronous methods MUST use "Async" suffix and return Task or ValueTask

**Rationale**: Consistent organization enables efficient navigation, maintenance, and testing while supporting cross-platform scenarios and dependency injection.

## Quality Standards

All code MUST meet the following quality standards:
- Comprehensive unit test coverage (>90%)
- Integration tests for Git interoperability
- Performance benchmarks against native Git and LibGit2Sharp
- Memory leak detection and prevention
- Cross-platform compatibility (.NET 8/9, Windows/Linux/macOS)

## Security Requirements

GitDotNet MUST implement secure practices:
- Input validation for all Git data parsing
- Safe handling of symbolic links and file paths
- Protection against malicious repository content
- Secure temporary file handling
- No sensitive data logging

## Development Workflow

All development MUST follow these practices:
- Test-Driven Development (TDD) for new features
- Performance regression testing for changes
- Git compatibility validation through integration tests
- Code review requirement for all changes
- Continuous integration with multiple .NET versions

## Governance

This constitution supersedes all other development practices. All pull requests and code reviews MUST verify compliance with these principles. Any deviation requires explicit justification and approval.

Performance benchmarks MUST be maintained and updated with each release. Compatibility with Git MUST be verified through comprehensive integration testing.

**Version**: 1.1.0 | **Ratified**: 2025-01-02 | **Last Amended**: 2025-01-02