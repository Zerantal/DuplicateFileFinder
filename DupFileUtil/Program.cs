using CommandLine;

using DupFileUtil.Commands;

using NLog;
using NLog.Config;
using NLog.Targets;

namespace DupFileUtil;

internal class Program
{
    public static void Main(string[] args)
    {
        var logConfig = new LoggingConfiguration();
        var logFile = new FileTarget("logfile")
        {
            FileName = "log.txt"
        };
        logConfig.AddRule(LogLevel.Info, LogLevel.Fatal, logFile);
        LogManager.Configuration = logConfig;

        Parser.Default.ParseArguments<ScanCommand, MarkCommand>(args).WithParsed<ICommand>(t => t.Execute());

        logFile.Dispose();
    }
}
