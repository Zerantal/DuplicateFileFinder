using Repo = DuplicateFileFinderLib.Repository.Core.Repo;

var repo = await Repo.OpenAsync("/home/z/.local/share/DuplicateFileFinder/repo");

// await repo.RepairRepoAsync();

var issues = repo.ValidateIntegrity(deepConsistencyCheck: true);
foreach (var issue in issues)
    Console.WriteLine(issue);
