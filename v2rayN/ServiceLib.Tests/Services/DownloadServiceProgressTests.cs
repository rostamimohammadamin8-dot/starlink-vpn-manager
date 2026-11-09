namespace ServiceLib.Tests.Services;

public class DownloadServiceProgressTests
{
    [Test]
    [Arguments(0x05, 0x00, 2, true)]
    [Arguments(0x05, 0xFF, 2, false)]
    [Arguments(0x05, 0x02, 2, false)]
    [Arguments(0x04, 0x00, 2, false)]
    [Arguments(0x05, 0x00, 1, false)]
    public async Task SocksReplyCheck_ShouldRequireNoAuthenticationMethod(byte version, byte method, int length, bool expected)
    {
        var check = typeof(DownloadService).GetMethod("IsSocks5NoAuthReply", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = (bool)check.Invoke(null, [new byte[] { version, method }, length])!;
        await result.Should().BeEqualTo(expected);
    }

    [Test]
    public async Task DownloadDataAsync_ShouldAwaitAsynchronousProgressCallbacks()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCallbackToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = false;
        Func<Action<string>, CancellationToken, Task> fakeDownload = (onProgress, _) =>
        {
            onProgress("progress");
            return Task.CompletedTask;
        };
        var runDownload = typeof(DownloadService).GetMethod("RunDownloadWithProgressAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        var downloadTask = (Task)runDownload.Invoke(null,
        [
            fakeDownload,
            (Func<bool, string, Task>)(async (_, _) =>
            {
                callbackStarted.TrySetResult();
                await allowCallbackToFinish.Task;
                callbackFinished = true;
            }),
            CancellationToken.None
        ])!;

        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await downloadTask.IsCompleted.Should().BeFalse();
        }
        finally
        {
            allowCallbackToFinish.TrySetResult();
        }

        await downloadTask.WaitAsync(TimeSpan.FromSeconds(10));
        await callbackFinished.Should().BeTrue();
    }

    [Test]
    public async Task DownloadDataAsync_ShouldAwaitProgressCallbacksWhenDownloadFails()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCallbackToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = false;
        var downloadException = new InvalidOperationException("download failed");
        Func<Action<string>, CancellationToken, Task> fakeDownload = (onProgress, _) =>
        {
            onProgress("progress");
            throw downloadException;
        };
        var runDownload = typeof(DownloadService).GetMethod("RunDownloadWithProgressAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        var downloadTask = (Task)runDownload.Invoke(null,
        [
            fakeDownload,
            (Func<bool, string, Task>)(async (_, _) =>
            {
                callbackStarted.TrySetResult();
                await allowCallbackToFinish.Task;
                callbackFinished = true;
            }),
            CancellationToken.None
        ])!;

        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await downloadTask.IsCompleted.Should().BeFalse();
        }
        finally
        {
            allowCallbackToFinish.TrySetResult();
        }

        var exceptionObserved = false;
        try
        {
            await downloadTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, downloadException))
        {
            exceptionObserved = true;
        }

        await callbackFinished.Should().BeTrue();
        await exceptionObserved.Should().BeTrue();
    }
}
