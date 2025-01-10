namespace GitDotNet.Tests.Helpers;

public static class TestUtils
{
    public static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var directory = new DirectoryInfo(path) { Attributes = FileAttributes.Normal };

        foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
        {
            info.Attributes = FileAttributes.Normal;
        }

        directory.Delete(true);
    }
}
