// Logging/LoggingSetup.cs

using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;
// ReSharper disable StringLiteralTypo

namespace DuplicateFileFinderLib.Logging;

public static class LoggingSetup
{
    public static void Configure(string appName = "DuplicateFileFinder")
    {
        var cfg = new LoggingConfiguration();
        
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appName, "logs");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(Path.Combine(baseDir, "archive"));

        // Rolling file target (daily + size-based, keep last 14 files)
        var file = new FileTarget("file")
        {
            FileName = Path.Combine(baseDir, "${shortdate}.log"),
            ArchiveFileName = Path.Combine(baseDir, "archive", "${shortdate}.{#}.log"),
            ArchiveEvery = FileArchivePeriod.Day,
            ArchiveAboveSize = 10 * 1024 * 1024, // 10 MB
            ArchiveNumbering = ArchiveNumberingMode.Rolling,
            MaxArchiveFiles = 14,
            ConcurrentWrites = true,
            KeepFileOpen = false,
            Encoding = System.Text.Encoding.UTF8,
            Layout = new JsonLayout
            {
                IncludeScopeProperties = true,
                Attributes =
                {
                    new JsonAttribute("ts", "${longdate}"),
                    new JsonAttribute("level", "${level:uppercase=true}"),
                    new JsonAttribute("logger", "${logger}"),
                    new JsonAttribute("msg", "${message}"),
                    new JsonAttribute("root", "${rootPath}"),
                    new JsonAttribute("phase", "${phase}"),
                    new JsonAttribute("threadid", "${threadid}"),
                    new JsonAttribute("exception", "${exception:format=ToString}")
                }
            }
            // Layout = 
            //     "${longdate}|${level:uppercase=true}|scan=${scopeproperty:scanId}|root=${scopeproperty:root}|${logger}|${message} ${exception:format=ToString}"
        };

        // Wrap file with async to avoid blocking UI/worker threads under load
        var asyncFile = new AsyncTargetWrapper(file)
        {
            QueueLimit = 10000,
            OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
            BatchSize = 50,
            TimeToSleepBetweenBatches = 50
        };
        cfg.AddTarget(asyncFile);
        cfg.AddRule(LogLevel.Info, LogLevel.Fatal, asyncFile);

        // Optional console (useful on Linux; harmless on Windows console runs)
        var console = new ConsoleTarget("console")
        {
            DetectConsoleAvailable = true,
            Layout = "${longdate}|${level:uppercase=true}|${message} ${exception:format=Message,Type}"
        };
        cfg.AddTarget(console);
        cfg.AddRule(LogLevel.Warn, LogLevel.Fatal, console);

        LogManager.Configuration = cfg;
        LogManager.ReconfigExistingLoggers();
    }
}
