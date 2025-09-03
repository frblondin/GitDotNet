using System.Collections.Immutable;
using System.Collections.Specialized;
using GitDotNet.Tools;
using Microsoft.Extensions.Logging;

namespace GitDotNet;

internal partial class GitConnectionInternal
{
    public async Task<CommitEntry> CommitAsync(string message, Signature? author = null, Signature? committer = null, CommitOptions? options = null)
    {
        _logger?.LogInformation("Committing staged changes. Message: {Message}", message);
        if (Info.Config.IsBare)
        {
            _logger?.LogWarning("Cannot commit to a bare repository.");
            throw new InvalidOperationException("Cannot commit to a bare repository.");
        }
        if (options is { UpdateBranch: false })
        {
            _logger?.LogWarning("Cannot commit without updating the branch.");
            throw new InvalidOperationException("Cannot commit without updating the branch.");
        }
        var escapedMessage = message.Replace("\"", "\\\"").Replace("\n", "\\n");
        var command = $"commit -m \"{escapedMessage}\"";
        if (author != null) command += $" --author=\"{author.Name} <{author.Email}>\"";
        _logger?.LogDebug("Executing git command: {Command}", command);
        GitCliCommand.Execute(Info.RootFilePath, command, updateEnvironmentVariables: AddCommitter);
        (Objects as IObjectResolverInternal)?.PackManager.UpdateIndices(force: true);
        var tip = await Head.GetTipAsync().ConfigureAwait(false);
        _logger?.LogInformation("Commit completed. New tip: {TipId}", tip.Id);
        return tip;
        void AddCommitter(StringDictionary env)
        {
            if (committer is null) return;
            env["GIT_COMMITTER_NAME"] = committer.Name;
            env["GIT_COMMITTER_EMAIL"] = committer.Email;
            env["GIT_COMMITTER_DATE"] = committer.Timestamp.ToUnixTimeSeconds().ToString();
            _logger?.LogDebug("Set committer environment: {CommitterName} <{CommitterEmail}>", committer.Name, committer.Email);
        }
    }

    public async Task<CommitEntry> CommitAsync(string branchName, Action<ITransformationComposer> transformations, CommitEntry commit, CommitOptions? options = null) =>
        await CommitAsync(branchName, c =>
        {
            _logger?.LogDebug("Applying transformations for branch: {BranchName}", branchName);
            transformations(c);
            return Task.FromResult(c);
        }, commit, options).ConfigureAwait(false);

    public async Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, Task> transformations, CommitEntry commit, CommitOptions? options = null)
    {
        if (options is { AmendPreviousCommit: true })
        {
            _logger?.LogWarning("Cannot amend previous commit using this method.");
            throw new InvalidOperationException("Cannot amend previous commit using this method.");
        }
        var canonicalName = branchName.LooksLikeLocalBranch() ? branchName : $"{Reference.LocalBranchPrefix}{branchName}";
        _logger?.LogInformation("Committing to branch: {CanonicalName}", canonicalName);
        var composer = _transformationComposerFactory(Info);
        await transformations(composer).ConfigureAwait(false);
        var result = await composer.CommitAsync(canonicalName, commit, options).ConfigureAwait(false);
        if (options?.UpdateBranch ?? true)
        {
            _logger?.LogDebug("Updating branch reference for {CanonicalName} to commit {CommitId}", canonicalName, result);
            _branchRefWriter.CreateOrUpdateLocalBranch(canonicalName, result, allowOverwrite: true);
        }
        else
        {
            _logger?.LogDebug("Skipping branch update for {CanonicalName}. Commit: {CommitId}", canonicalName, result);
        }
        if (options?.UpdateHead ?? true)
        {
            _headWriter.UpdateHead(canonicalName);
        }
        else
        {
            _logger?.LogDebug("Skipping HEAD update for {CanonicalName}. Commit: {CommitId}", canonicalName, result);
        }
        (Objects as IObjectResolverInternal)?.PackManager.UpdateIndices(force: true);
        var committed = await Objects.GetAsync<CommitEntry>(result!).ConfigureAwait(false);
        _logger?.LogInformation("Commit to branch {CanonicalName} completed. Commit: {CommitId}", canonicalName, committed.Id);
        return committed;
    }

    public CommitEntry CreateCommit(string message, IList<CommitEntry> parents, Signature? author = null, Signature? committer = null) =>
        new(HashId.Empty, [], Objects)
        {
            _content = new(new CommitEntry.Content("", author ?? Info.Config.CreateSignature(), committer ?? Info.Config.CreateSignature(), [], message)),
            ParentIds = parents.Select(p => p.Id).ToImmutableList(),
        };
}