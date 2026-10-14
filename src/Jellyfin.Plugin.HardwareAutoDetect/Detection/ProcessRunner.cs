using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.HardwareAutoDetect.Detection;

/// <summary>
/// Result of running an external process.
/// </summary>
/// <param name="ExitCode">Exit code (-1 when it timed out or failed to start).</param>
/// <param name="Output">Combined stdout + stderr (truncated).</param>
/// <param name="TimedOut">Whether the process was killed for running too long.</param>
public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Abstraction over process execution so the probing logic can be unit tested.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Runs real processes.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputChars = 8192;

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(line);
                }
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, output.ToString(), true);
        }

        // Make sure async output handlers have drained.
        process.WaitForExit();
        lock (output)
        {
            return new ProcessResult(process.ExitCode, output.ToString(), false);
        }
    }
}
