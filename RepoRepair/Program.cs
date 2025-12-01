using DuplicateFileFinderLib.Repository;

var repo = await Repo.OpenAsync("/home/z/.local/share/DuplicateFileFinder/repo");

repo.RepairMigratedRepo();

var issues = repo.ValidateIntegrity(deepConsistencyCheck: false);
foreach (var issue in issues)
    Console.WriteLine(issue);