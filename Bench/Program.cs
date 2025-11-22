using Bench;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository;
using NLog;

var log = LogManager.GetCurrentClassLogger();
var root = args.Length > 1 && args[0] == "--root" ? args[1] : args.FirstOrDefault() ?? ".";

root = Path.GetFullPath(root);

TimingLog.AddCounterFormatter("AggregateSize", n => n.ToSizeString());

var repoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bench", "repo");

if (Directory.Exists(repoDir))
    Directory.Delete(repoDir, true);

var repo = Repo.Open(repoDir);
var finder = new DuplicateFileFinder(repo);

log.Info($"Bench location: {root}", root);
using (TimingLog.Start("Folder scan", root))
{
    await finder.ScanLocationAsync(root, null, CancellationToken.None);
}

repo.CompactNow();