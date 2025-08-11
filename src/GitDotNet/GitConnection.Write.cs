using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using GitDotNet.Tools;

namespace GitDotNet;

public partial class GitConnection
{
    /// <summary>
    /// Commits staged changes asynchronously returns the resulting commit entry.
    /// </summary>
    /// <param name="message">The commit message describing the changes made.</param>
    /// <param name="author">An optional signature representing the author of the commit.</param>
    /// <param name="committer">The committer of the commit.</param>
    /// <param name="options">The <see cref="CommitOptions"/> that specify the commit behavior.</param>
    /// <returns>Returns a task that resolves to a CommitEntry representing the committed changes.</returns>
    public async Task<CommitEntry> CommitAsync(string message, Signature? author = null, Signature? committer = null, CommitOptions? options = null)
    {
        if (Info.Config.IsBare) throw new InvalidOperationException("Cannot commit to a bare repository.");
        if (options != null && !options.UpdateBranch) throw new InvalidOperationException("Cannot commit without updating the branch.");

        // Build the CLI command
        var escapedMessage = message.Replace("\"", "\\\"").Replace("\n", "\\n");
        var command = $"commit -m \"{escapedMessage}\"";
        if (author != null) command += $" --author=\"{author.Name} <{author.Email}>\"";

        // Execute the CLI command and get the output
        GitCliCommand.Execute(Info.RootFilePath, command, updateEnvironmentVariables: AddCommitter);
        (Objects as IObjectResolverInternal)?.PackManager.UpdatePacks(force: true);

        return await Head.GetTipAsync();

        void AddCommitter(StringDictionary env)
        {
            if (committer is null) return;
            env["GIT_COMMITTER_NAME"] = committer.Name;
            env["GIT_COMMITTER_EMAIL"] = committer.Email;
            env["GIT_COMMITTER_DATE"] = committer.Timestamp.ToUnixTimeSeconds().ToString();
        }
    }


    /// <summary>
    /// Commits the changes in the transformation composer to the repository.
    /// This method is usually used for bare repositories.
    /// </summary>
    /// <param name="branchName">The branch name to commit to.</param>
    /// <param name="transformations">The transformations to apply to the repository.</param>
    /// <param name="commit">The commit entry to commit.</param>
    /// <param name="options">The <see cref="CommitOptions"/> that specify the commit behavior.</param>
    public async Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, ITransformationComposer> transformations, CommitEntry commit, CommitOptions? options = null) =>
        await CommitAsync(branchName, c =>
        {
            transformations(c);
            return Task.FromResult(c);
        }, commit, options);

    /// <summary>
    /// Commits the changes in the transformation composer to the repository.
    /// This method is usually used for bare repositories.
    /// </summary>
    /// <param name="branchName">The branch name to commit to.</param>
    /// <param name="transformations">The transformations to apply to the repository.</param>
    /// <param name="commit">The commit entry to commit.</param>
    /// <param name="options">The <see cref="CommitOptions"/> that specify the commit behavior.</param>
    public async Task<CommitEntry> CommitAsync(string branchName, Func<ITransformationComposer, Task<ITransformationComposer>> transformations, CommitEntry commit, CommitOptions? options = null)
    {
        if (options != null && options.AmendPreviousCommit) throw new InvalidOperationException("Cannot amend previous commit using this method.");

        var canonicalName = Reference.LooksLikeLocalBranch(branchName) ? branchName : $"{Reference.LocalBranchPrefix}{branchName}";

        var composer = _transformationComposerFactory(Info.Path);
        await transformations(composer);

        var result = await composer.CommitAsync(canonicalName, commit, options);
        (Objects as IObjectResolverInternal)?.PackManager.UpdatePacks(force: true);

        return await Objects.GetAsync<CommitEntry>(result!);
    }

    /// <summary>Creates a new in-memory commit entry before it gets committed to repository.</summary>
    /// <param name="message">The commit message.</param>
    /// <param name="parents">The parent commits.</param>
    /// <param name="author">The author of the commit.</param>
    /// <param name="committer">The committer of the commit.</param>
    /// <returns>The new commit entry.</returns>
    public CommitEntry CreateCommit(string message, IList<CommitEntry> parents, Signature? author = null, Signature? committer = null) =>
        new(HashId.Empty, [], Objects)
        {
            _content = new(new CommitEntry.Content("", author ?? Info.Config.CreateSignature(), committer ?? Info.Config.CreateSignature(), [], message)),
            ParentIds = parents.Select(p => p.Id).ToImmutableList(),
        };
}