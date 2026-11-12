using System;
using System.Collections.Generic;

namespace WB.Logging;

internal sealed record LogMessage<TPayload> : ILogMessage<TPayload>
    where TPayload : notnull
{
    // ┌─────────────────────────────────────────────────────────────────────────────┐
    // │ Public Properties                                                           │
    // └─────────────────────────────────────────────────────────────────────────────┘

    /// <inheritdoc/>
    public required TPayload Payload { get; init; }

    /// <inheritdoc/>
    public required DateTimeOffset Timestamp { get; init; }
    
    /// <inheritdoc/>
    public required IReadOnlyList<string> Senders { get; init; }

    /// <inheritdoc/>
    public LogLevel? LogLevel { get; init; }

    /// <inheritdoc/>
    object ILogMessage.Payload => Payload;

}
