using System.Diagnostics;

namespace GitDotNet.Tools;

/// <summary>Provides a set of methods to interact with the Git CLI.</summary>
public static class GitCliCommand
{
    private static readonly Lazy<bool> _isGitInstalled = new(
        () => ExecuteNoCheck(Environment.CurrentDirectory, "--version", throwOnError: false) == 0);

    internal static string? GetAbsoluteGitPath(string path) =>
        ExecuteNoCheck(path, "rev-parse --absolute-git-dir", throwOnError: false, outputDataReceived: (_, e) =>
        {
            if (e.Data is not null)
            {
                path = e.Data;
            }
        }) == 0 ? path : null;

    /// <summary>Executes a GIT command.</summary>
    /// <param name="repository">The path of the repository.</param>
    /// <param name="arguments">The command to execute.</param>
    /// <param name="inputStream">The input stream (optional).</param>
    /// <param name="throwOnError">Whether an exception should be thrown if command failed.</param>
    /// <param name="outputDataReceived">
    /// Handler that will be invoked each time an application writes a line to its
    /// redirected <see cref="Process.StandardOutput"/> stream.
    /// </param>
    /// <returns>The command exit status.</returns>
    public static int Execute(string repository,
                              string arguments,
                              Stream? inputStream = null,
                              bool throwOnError = true,
                              DataReceivedEventHandler? outputDataReceived = null)
    {
        ThrowIfGitNotInstalled();

        return ExecuteNoCheck(repository, arguments, inputStream, throwOnError, outputDataReceived);
    }

    /// <summary>Gets a value indicating whether Git CLI is accessible.</summary>
    public static bool IsGitInstalled => _isGitInstalled.Value;

    internal static void ThrowIfGitNotInstalled()
    {
        if (!IsGitInstalled)
        {
            throw new InvalidOperationException("Git doesn't seem to be installed or is not accessible.");
        }
    }

    private static int ExecuteNoCheck(string repository,
                                      string arguments,
                                      Stream? inputStream = null,
                                      bool throwOnError = true,
                                      DataReceivedEventHandler? outputDataReceived = null)
    {
        var process = CreateProcess(repository, arguments, outputDataReceived);
        process.Start();
        if (process.StartInfo.RedirectStandardOutput)
        {
            process.BeginOutputReadLine();
        }

        if (inputStream is not null)
        {
            CopyStreamToInput(process, inputStream);
        }

        process.WaitForExit();

        if (throwOnError)
        {
            ThrowIfError(process);
        }

        return process.ExitCode;
    }

    private static Process CreateProcess(string repository, string arguments, DataReceivedEventHandler? outputDataReceived)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            Arguments = arguments,
            CreateNoWindow = false,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = outputDataReceived is not null,
        };
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = startInfo.RedirectStandardOutput,
        };
        if (outputDataReceived is not null)
        {
            process.OutputDataReceived += outputDataReceived;
        }

        return process;
    }

    private static void CopyStreamToInput(Process process, Stream stream)
    {
        try
        {
            stream.CopyTo(process.StandardInput.BaseStream);
        }
        catch
        {
            // Error message will be exposed by standard error
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private static void ThrowIfError(Process process)
    {
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Git command failed: " + error);
        }
    }
}
