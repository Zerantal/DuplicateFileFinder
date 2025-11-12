using Bench;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Util;
using NLog;

Logger log = LogManager.GetCurrentClassLogger();
var root = args.Length > 1 && args[0] == "--root" ? args[1] : args.FirstOrDefault() ?? ".";
root = PathUtils.NormalizePath(root);

TimingLog.AddCounterFormatter("AggregateSize", (n) => n.ToSizeString() );

var finder = new DuplicateFileFinder();
// var sw = Stopwatch.StartNew();
log.Info($"Bench location: {root}", root);
using (TimingLog.Start("Folder scan", root))
{
    await finder.ScanLocation(root, progressIndicator: null, CancellationToken.None);
}
