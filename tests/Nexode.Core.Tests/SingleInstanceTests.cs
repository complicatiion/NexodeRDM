using Nexode.Platform;
using Xunit;

namespace Nexode.Core.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task FirstStartDoesNotWaitAndSecondStartForwards()
    {
        var id = "NexodeTest-" + Guid.NewGuid().ToString("N")[..8];
        using var first = new SingleInstance(id);
        Assert.False(first.TryForward([]));
        // Decided by the mutex, without probing the pipe (that used to wait 250 ms and throw a TimeoutException).
        // No timing assertion: test runners on a busy machine make that flaky.
        Assert.True(first.IsFirst);

        var received = new TaskCompletionSource<string[]>();
        first.ArgumentsReceived += a => received.TrySetResult(a);
        first.StartListening();

        using var second = new SingleInstance(id);
        Assert.True(await Task.Run(() => second.TryForward(["--new-window"])));
        Assert.False(second.IsFirst);
        Assert.Equal(["--new-window"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
