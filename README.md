# WB.Logging

An easy-to-use non-blocking logging library for .NET applications, designed to provide a simple and efficient way to log messages with various levels of severity.

## ✨ Features

- **Non-blocking**: Logs are processed asynchronously, ensuring that your application remains responsive.
- **Multiple Sinks**: Supports multiple output targets (sinks) such as console, file, and more.
- **Configurable**: Easily configure log levels, formats, and sinks to suit your needs.
- **Extensible**: Create custom sinks and loggers to fit your specific requirements.
- **Log anything**: Log messages of any type, including complex objects.

## ⬇️ Log Sinks

| Sink Type      | Description                                          |
|----------------|------------------------------------------------------|
| ConsoleLogSink | Logs messages to the console using `System.Console`. |

**Note:** More log sinks will be added in the future, and you can also create your own custom sinks by implementing the `ILogSink` interface.

## 🚀 Example

```csharp
using WB.Logging;

// Step 1:  Create a logger with name "MyApp"
await using ILogger logger = new Logger("MyApp");

// Step 2:  Attach a console sink to the logger
using IDisposable attachment = logger.AttachConsole();

// Step 3:  Log messages with different severity levels
logger.LogInfo("This is an informational message.");
logger.LogWarning("This is a warning message.");
logger.LogError("This is an error message.");
```

**Notes:**

- Step 1: The `await using` statement in the example, ensures that the logger is properly disposed of after use, allowing it to flush any remaining log messages before the application exits.
- Step 2: The `IDisposable` returned by `AttachConsole()` can be used to detach the console sink when it's no longer needed, preventing further log messages from being sent to the console.
- Logging and flushing after disposal throw `ObjectDisposedException`. Dispose the logger only after producers have stopped.
