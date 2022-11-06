using CommandLine;
using DupFileUtil.Commands;
using NLog;

namespace DupFileUtil;

internal class Program
{

    public static void Main(string[] args)
    {
        var logConfig = new NLog.Config.LoggingConfiguration();
        var logFile = new NLog.Targets.FileTarget("logfile");
        logFile.FileName = "log.txt";
        logConfig.AddRule(LogLevel.Info, LogLevel.Fatal, logFile);
        LogManager.Configuration = logConfig;

        Parser.Default.ParseArguments<ScanCommand, MarkCommand>(args).WithParsed<ICommand>(t => t.Execute());

        logFile.Dispose();
    }
}