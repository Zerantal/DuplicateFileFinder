using Bench;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Util;
using NLog;

Logger log = LogManager.GetCurrentClassLogger();
var root = args.Length > 1 && args[0] == "--root" ? args[1] : args.FirstOrDefault() ?? ".";
root = PathUtils.NormalizePath(root);

TimingLog.AddCounterFormatter("AggregateSize", (n) => n.ToSizeString() );

var repoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bench", "repo");
Repo repo = Repo.Open(repoDir);
var finder = new DuplicateFileFinder(repo);

log.Info($"Bench location: {root}", root);
using (TimingLog.Start("Folder scan", root))
{
    await finder.ScanLocation(root, progressIndicator: null, CancellationToken.None);
}

repo.CompactNow();
