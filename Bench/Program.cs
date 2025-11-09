using System.Diagnostics;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Util;
using NLog;
using NLog.Fluent;

Logger log = LogManager.GetCurrentClassLogger();
var root = args.Length > 1 && args[0] == "--root" ? args[1] : args.FirstOrDefault() ?? ".";
root = PathUtils.NormalizePath(root);

var finder = new DuplicateFileFinder();
// var sw = Stopwatch.StartNew();
log.Info($"Bench location: {root}", root);
await finder.ScanLocation(root, progressIndicator: null, token: CancellationToken.None);
