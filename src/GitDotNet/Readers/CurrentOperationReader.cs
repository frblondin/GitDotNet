using System.Diagnostics.CodeAnalysis;
using GitDotNet.Data;

namespace GitDotNet.Readers;

internal delegate CurrentOperationReader CurrentOperationReaderFactory(IRepositoryInfo info);

[ExcludeFromCodeCoverage]
internal class CurrentOperationReader(IRepositoryInfo info)
{
    private const string SequencerFolder = "sequencer";

    public virtual CurrentOperation Read()
    {
        if (File.Exists(Path.Combine(info.Path, "MERGE_HEAD")))
            return CurrentOperation.Merge;
        if (File.Exists(Path.Combine(info.Path, "REVERT_HEAD")))
            return CurrentOperation.Revert;
        if (File.Exists(Path.Combine(info.Path, "CHERRY_PICK_HEAD")))
            return CurrentOperation.CherryPick;
        if (File.Exists(Path.Combine(info.Path, "BISECT_LOG")))
            return CurrentOperation.Bisect;
        if (Directory.Exists(Path.Combine(info.Path, "rebase-apply")))
        {
            return File.Exists(Path.Combine(info.Path, "rebase-apply", "interactive")) ?
                CurrentOperation.RebaseInteractive :
                CurrentOperation.Rebase;
        }
        if (Directory.Exists(Path.Combine(info.Path, "rebase-merge")))
            return CurrentOperation.RebaseMerge;
        if (Directory.Exists(Path.Combine(info.Path, SequencerFolder)))
        {
            var state = CheckSequencerState();
            if (state.HasValue)
            {
                return state.Value;
            }
        }
        if (File.Exists(Path.Combine(info.Path, "apply-mailbox")))
            return CurrentOperation.ApplyMailbox;
        if (File.Exists(Path.Combine(info.Path, "apply-mailbox-or-rebase")))
            return CurrentOperation.ApplyMailboxOrRebase;

        return CurrentOperation.None;
    }

    private CurrentOperation? CheckSequencerState()
    {
        if (File.Exists(Path.Combine(info.Path, SequencerFolder, "rebase-apply")))
            return CurrentOperation.Rebase;
        if (File.Exists(Path.Combine(info.Path, SequencerFolder, "rebase-merge")))
            return CurrentOperation.RebaseMerge;
        if (File.Exists(Path.Combine(info.Path, SequencerFolder, "cherry-pick")))
            return CurrentOperation.CherryPickSequence;
        if (File.Exists(Path.Combine(info.Path, SequencerFolder, "revert")))
            return CurrentOperation.RevertSequence;
        return null;
    }
}
