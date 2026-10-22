using Olive.Application;

namespace Olive.Host.Alarms;

// D-08: a Critical log event carrying the alarm code and component only, never request data.
public sealed class LoggingOperationalAlarm(ILoggerFactory loggerFactory) : IOperationalAlarm
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Olive.Alarm");

    public void Raise(string code, string component) =>
        _logger.LogCritical("Operational alarm {AlarmCode} component={Component}", code, component);
}
