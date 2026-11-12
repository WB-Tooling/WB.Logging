using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using WB.Logging;

namespace LoggerTests.MethodTests.LogMethodTests;

internal sealed class TestLogSink : ILogSink
{
    public readonly List<ILogMessage> ReceivedMessages = [];

    public void Submit<TPayload>(ILogMessage<TPayload> logMessage)
        where TPayload : notnull
        => ReceivedMessages.Add(logMessage);
}

internal sealed class SenderGateLogSink(string gatePayload) : IAsyncLogSink
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => entered.Task;

    public void Release()
        => released.TrySetResult();

    public async ValueTask SubmitAsync<TPayload>(ILogMessage<TPayload> logMessage, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        if (Equals(logMessage.Payload, gatePayload))
        {
            entered.TrySetResult();
            await released.Task.ConfigureAwait(false);
        }
    }
}

internal sealed class SenderSnapshotLogSink(string targetPayload) : ILogSink
{
    public readonly List<IReadOnlyList<string>> SenderSnapshots = [];
    public readonly List<ILogMessage> TargetMessages = [];
    public readonly List<object> ReceivedPayloads = [];

    public void Submit<TPayload>(ILogMessage<TPayload> logMessage)
        where TPayload : notnull
    {
        ReceivedPayloads.Add(logMessage.Payload);

        if (Equals(logMessage.Payload, targetPayload))
        {
            SenderSnapshots.Add(logMessage.Senders.ToArray());
            TargetMessages.Add(logMessage);
        }
    }
}

public sealed class TheLogMethod
{
    [Test]
    public async Task ShouldSubmitTheLogMessageToAllAttachedLogSinks()
    {
        // Arrange
        string loggerName = "TestLogger";
        TestLogSink testLogSink1 = new();
        TestLogSink testLogSink2 = new();
        await using Logger logger = new(loggerName);
        ILogger childLogger = logger.CreateChildLogger("ChildLogger");
        logger.AttachLogSink(testLogSink1);
        logger.AttachLogSink(testLogSink2);

        // Act
        childLogger.Log(LogLevel.Info, "Hello, world.");
        await childLogger.FlushAsync().ConfigureAwait(false);

        // Assert
        testLogSink1.ReceivedMessages.Should().ContainSingle()
            .Which.Payload.Should().Be("Hello, world.");
        testLogSink2.ReceivedMessages.Should().ContainSingle()
            .Which.Payload.Should().Be("Hello, world.");
    }

    [Test]
    [Arguments(null)]
    [Arguments(LogLevel.Info)]
    [Arguments(LogLevel.Warning)]
    [Arguments(LogLevel.Error)]
    public async Task ShouldLogEachLogLevel(LogLevel? logLevel)
    {
        // Arrange
        string loggerName = "TestLogger";
        TestLogSink testLogSink = new();
        await using Logger logger = new(loggerName);
        logger.AttachLogSink(testLogSink);

        // Act
        logger.Log(logLevel ?? LogLevel.Info, "Hello, world.");
        await logger.FlushAsync().ConfigureAwait(false);

        // Assert
        testLogSink.ReceivedMessages.Should().ContainSingle()
            .Which.LogLevel.Should().Be(logLevel ?? LogLevel.Info);
    }

    [Test]
    public async Task ShouldThrowWhenLoggingAfterDisposal()
    {
        // Arrange
        Logger logger = new("TestLogger");
        await logger.DisposeAsync().ConfigureAwait(false);

        // Act
        Action log = () => logger.Log(LogLevel.Info, "after-disposal");

        // Assert
        log.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task ShouldDeliverEveryMessageFromConcurrentProducers()
    {
        // Arrange
        const int producerCount = 8;
        const int messagesPerProducer = 100;
        TestLogSink testLogSink = new();
        await using Logger logger = new("TestLogger");
        logger.AttachLogSink(testLogSink);

        string[] expectedMessages = Enumerable.Range(0, producerCount)
            .SelectMany(producer => Enumerable.Range(0, messagesPerProducer)
                .Select(message => $"{producer}:{message}"))
            .ToArray();

        // Act
        Parallel.For(0, producerCount, producer =>
        {
            for (int message = 0; message < messagesPerProducer; message++)
            {
                logger.Log(LogLevel.Info, $"{producer}:{message}");
            }
        });
        await logger.FlushAsync().ConfigureAwait(false);

        // Assert
        testLogSink.ReceivedMessages.Should().HaveCount(expectedMessages.Length);
        testLogSink.ReceivedMessages
            .Select(logMessage => logMessage.Payload)
            .Should()
            .BeEquivalentTo(expectedMessages);
    }

    [Test]
    public async Task ShouldExposeCompleteStableSenderMetadataToEverySink()
    {
        // Arrange
        const string targetPayload = "sender-metadata-target";
        Logger parentLogger = new("ParentLogger");
        Logger childLogger = (Logger)parentLogger.CreateChildLogger("ChildLogger");
        SenderGateLogSink parentGate = new("parent-gate");
        SenderGateLogSink childGate = new("child-gate");
        SenderSnapshotLogSink parentObserver = new(targetPayload);
        SenderSnapshotLogSink childObserver = new(targetPayload);
        parentLogger.AttachLogSink(parentGate);
        childLogger.AttachLogSink(childGate);
        parentLogger.AttachLogSink(parentObserver);
        childLogger.AttachLogSink(childObserver);

        try
        {
            parentLogger.Log(LogLevel.Info, "parent-gate");
            await parentGate.Entered.ConfigureAwait(false);
            childLogger.Log(LogLevel.Info, "child-gate");
            await childGate.Entered.ConfigureAwait(false);

            childLogger.Log(LogLevel.Info, targetPayload);

            // Act
            parentGate.Release();
            childGate.Release();
            await childLogger.FlushAsync().ConfigureAwait(false);
            await parentLogger.FlushAsync().ConfigureAwait(false);

            // Assert
            string[] expectedSenders = ["ChildLogger", "ParentLogger"];
            foreach (SenderSnapshotLogSink observer in new[] { parentObserver, childObserver })
            {
                observer.ReceivedPayloads.Should().Contain(targetPayload);
                observer.SenderSnapshots.Should().ContainSingle()
                    .Which.Should().Equal(expectedSenders);
            }

            // A later message must not mutate metadata already observed for this record.
            childLogger.Log(LogLevel.Info, "subsequent-message");
            await childLogger.FlushAsync().ConfigureAwait(false);

            foreach (SenderSnapshotLogSink observer in new[] { parentObserver, childObserver })
            {
                observer.TargetMessages.Should().ContainSingle()
                    .Which.Senders.Should().Equal(expectedSenders);
            }

        }
        finally
        {
            parentGate.Release();
            childGate.Release();
            await parentLogger.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ShouldForwardToEveryAncestorWithCompleteSenderMetadata()
    {
        await using Logger root = new("Root");
        Logger middle = (Logger)root.CreateChildLogger("Middle");
        Logger leaf = (Logger)middle.CreateChildLogger("Leaf");
        TestLogSink rootSink = new();
        TestLogSink middleSink = new();
        TestLogSink leafSink = new();
        root.AttachLogSink(rootSink);
        middle.AttachLogSink(middleSink);
        leaf.AttachLogSink(leafSink);

        leaf.Log(LogLevel.Info, "nested");
        await leaf.FlushAsync().ConfigureAwait(false);

        foreach (TestLogSink sink in new[] { rootSink, middleSink, leafSink })
        {
            sink.ReceivedMessages.Should().ContainSingle()
                .Which.Senders.Should().Equal("Leaf", "Middle", "Root");
        }
    }
}
