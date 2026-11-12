using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using WB.Logging;

namespace LoggerTests.MethodTests.CreateChildLoggerMethodTests;

internal sealed class TestLogSink : ILogSink, IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose()
        => IsDisposed = true;

    public void Submit<TPayload>(ILogMessage<TPayload> logMessage)
        where TPayload : notnull
        => throw new NotImplementedException("This test log sink should not be used to submit log messages.");
}

internal sealed class AsyncTestLogSink : IAsyncLogSink, IAsyncDisposable
{
    public bool IsDisposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask SubmitAsync<TPayload>(ILogMessage<TPayload> logMessage, CancellationToken cancellationToken)
        where TPayload : notnull
        => throw new NotImplementedException("This test log sink should not be used to submit log messages.");
}

internal sealed class BlockingLogSink : ILogSink
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim release = new();
    private int hasBlocked;

    public readonly List<ILogMessage> ReceivedMessages = [];

    public Task Entered => entered.Task;

    public void Release()
        => release.Set();

    public void Submit<TPayload>(ILogMessage<TPayload> logMessage)
        where TPayload : notnull
    {
        if (Interlocked.Exchange(ref hasBlocked, 1) == 0)
        {
            entered.TrySetResult();
            release.Wait();
        }

        ReceivedMessages.Add(logMessage);
    }
}

internal sealed class CancellationAwareLogSink : IAsyncLogSink
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => entered.Task;

    public bool ReceivedMessage { get; private set; }

    public void Release() => release.TrySetResult();

    public async ValueTask SubmitAsync<TPayload>(ILogMessage<TPayload> logMessage, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        entered.TrySetResult();
        await release.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ReceivedMessage = true;
    }
}

public sealed class TheDisposeAsyncMethod
{
    [Test]
    public async Task ShouldDisposeTheLogger()
    {
        // Arrange
        Logger logger = new("TestLogger");

        // Act
        await logger.DisposeAsync().ConfigureAwait(false);

        // Assert
        logger.IsDisposed.Should().BeTrue();
    }

    [Test]
    public async Task ShouldDisposeAllOfChildLoggers()
    {
        // Arrange
        Logger parentLogger = new("ParentLogger");
        Logger childLogger1 = parentLogger.CreateChildLogger("ChildLogger1") as Logger ?? throw new InvalidOperationException("CreateChildLogger should return a Logger instance.");
        Logger childLogger2 = childLogger1.CreateChildLogger("ChildLogger2") as Logger ?? throw new InvalidOperationException("CreateChildLogger should return a Logger instance.");

        // Act
        await parentLogger.DisposeAsync().ConfigureAwait(false);

        // Assert
        childLogger1.IsDisposed.Should().BeTrue();
        childLogger2.IsDisposed.Should().BeTrue();
    }

    [Test]
    public async Task ShouldDisposeAllLogSinks()
    {
        // Arrange
        TestLogSink testLogSink1 = new();
        AsyncTestLogSink testLogSink2 = new();
        await using Logger logger = new("TestLogger");
        logger.AttachLogSink(testLogSink1);
        logger.AttachLogSink(testLogSink2);

        // Act
        await logger.DisposeAsync().ConfigureAwait(false);

        // Assert
        testLogSink1.IsDisposed.Should().BeTrue();
        testLogSink2.IsDisposed.Should().BeTrue();
    }

    [Test]
    public async Task ShouldDrainQueuedLogMessagesBeforeDisposing()
    {
        // Arrange
        Logger logger = new("TestLogger");
        BlockingLogSink testLogSink = new();
        logger.AttachLogSink(testLogSink);
        string[] expectedMessages = ["first", "second", "third"];

        try
        {
            logger.Log(LogLevel.Info, expectedMessages[0]);
            await testLogSink.Entered.ConfigureAwait(false);

            foreach (string message in expectedMessages.Skip(1))
            {
                logger.Log(LogLevel.Info, message);
            }

            // Act
            ValueTask disposeTask = logger.DisposeAsync();
            testLogSink.Release();
            await disposeTask.ConfigureAwait(false);

            // Assert
            testLogSink.ReceivedMessages
                .Select(logMessage => logMessage.Payload)
                .Should()
                .BeEquivalentTo(expectedMessages);
        }
        finally
        {
            testLogSink.Release();

            if (!logger.IsDisposed)
            {
                await logger.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task ShouldAllowInFlightAsyncSinkToCompleteDuringDisposal()
    {
        Logger logger = new("TestLogger");
        CancellationAwareLogSink sink = new();
        logger.AttachLogSink(sink);

        try
        {
            logger.Log(LogLevel.Info, "message");
            await sink.Entered.ConfigureAwait(false);

            ValueTask firstDisposal = logger.DisposeAsync();
            ValueTask secondDisposal = logger.DisposeAsync();
            secondDisposal.IsCompleted.Should().BeFalse();
            sink.Release();
            await firstDisposal.ConfigureAwait(false);
            await secondDisposal.ConfigureAwait(false);

            sink.ReceivedMessage.Should().BeTrue();
        }
        finally
        {
            sink.Release();
            await logger.DisposeAsync().ConfigureAwait(false);
        }
    }
}
