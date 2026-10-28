// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Mxc.Sdk.V1;
using Xunit;

namespace Microsoft.Mxc.Sdk.Tests.V1;

public class MxcContainerBubblewrapE2ETests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void Spawn_OutlivesTheThreadThatSpawnedIt()
    {
        // `bwrap --die-with-parent` arms PR_SET_PDEATHSIG, which Linux fires
        // when the *thread* that forked bwrap exits. A caller that spawns on a
        // thread-pool or short-lived thread and waits elsewhere must not have
        // the sandbox killed when that thread exits.
        BubblewrapHost.Require();

        var proc = SpawnOnShortLivedThread();
        StreamReader? stdout = null;
        MxcStreamCloser? stdoutCloser = null;
        var completed = false;
        try
        {
            stdout = new StreamReader(proc.StandardOutput!, Encoding.UTF8);
            stdoutCloser = proc.StandardOutputCloser;
            // The workload is running, so bwrap has armed its parent-death signal.
            Assert.Equal("ready", ReadLineWithin(stdout, OperationTimeout));
            Assert.Equal("survived", ReadToEndWithin(stdout, OperationTimeout).Trim());

            var result = proc.Wait();
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            completed = true;
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    stdoutCloser?.Close();
                    proc.Kill();
                }
            }
            finally
            {
                proc.Dispose();
                stdoutCloser?.Dispose();
                stdout?.Dispose();
            }
        }
    }

    private static MxcProcess SpawnOnShortLivedThread()
    {
        var completion = new TaskCompletionSource<MxcProcess>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var spawner = new Thread(() =>
        {
            try
            {
                var proc = MxcContainer.Spawn(
                    new ContainerRequest("echo ready; sleep 1; echo survived")
                    {
                        Containment = new Containment.Bubblewrap(),
                        Filesystem = new FilesystemPolicy { ReadwritePaths = ["/tmp"] },
                        TimeoutMs = 30_000,
                    });
                if (!completion.TrySetResult(proc))
                {
                    proc.Dispose();
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "mxc-bwrap-test-spawner",
        };
        spawner.Start();

        var completed = Task.WhenAny(completion.Task, Task.Delay(OperationTimeout))
            .GetAwaiter()
            .GetResult() == completion.Task;
        if (!completed)
        {
            if (!completion.TrySetCanceled() && completion.Task.IsCompletedSuccessfully)
            {
                completion.Task.GetAwaiter().GetResult().Dispose();
            }
            throw new TimeoutException(
                $"Bubblewrap spawn thread did not finish within {OperationTimeout}.");
        }

        var proc = completion.Task.GetAwaiter().GetResult();
        if (!spawner.Join(OperationTimeout))
        {
            proc.Dispose();
            throw new TimeoutException(
                $"Bubblewrap spawn thread did not exit within {OperationTimeout}.");
        }
        return proc;
    }

    private static string ReadLineWithin(StreamReader reader, TimeSpan timeout)
    {
        var read = Task.Run(reader.ReadLine);
        Assert.True(read.Wait(timeout), $"stdout did not produce its first line within {timeout}.");
        return read.GetAwaiter().GetResult() ?? string.Empty;
    }

    private static string ReadToEndWithin(StreamReader reader, TimeSpan timeout)
    {
        var read = Task.Run(reader.ReadToEnd);
        Assert.True(read.Wait(timeout), $"stdout did not reach EOF within {timeout}.");
        return read.GetAwaiter().GetResult();
    }
}
