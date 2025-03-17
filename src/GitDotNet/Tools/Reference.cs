namespace GitDotNet.Tools;

internal static class Reference
{
    internal const string LocalBranchPrefix = "refs/heads/";

    internal const string RemoteTrackingBranchPrefix = "refs/remotes/";

    internal const string TagPrefix = "refs/tags/";

    internal static bool LooksLikeLocalBranch(this string canonicalName) =>
        canonicalName.IsPrefixedBy(LocalBranchPrefix);

    internal static bool LooksLikeRemoteTrackingBranch(this string canonicalName) =>
        canonicalName.IsPrefixedBy(RemoteTrackingBranchPrefix);

    internal static bool LooksLikeTag(this string canonicalName) =>
        canonicalName.IsPrefixedBy(TagPrefix);

    private static bool IsPrefixedBy(this string input, string prefix) =>
        input.StartsWith(prefix, StringComparison.Ordinal);
}
