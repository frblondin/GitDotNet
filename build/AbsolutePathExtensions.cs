using Nuke.Common.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

internal static class AbsolutePathExtensions
{
    internal static string ToGitPath(this string path, AbsolutePath basePath) =>
        path
        .Replace(basePath.ToString(), "")
        .Replace('\\', '/')
        .TrimStart('/');
}