using Repo = DuplicateFileFinderLib.Repository.Core.Repo;

namespace RepoCompareTool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: repo-compare <repoA> <repoB>");
            return 1;
        }

        var pathA = args[0];
        var pathB = args[1];

        if (!Directory.Exists(pathA))
        {
            Console.Error.WriteLine($"Left repo directory does not exist: {pathA}");
            return 1;
        }

        if (!Directory.Exists(pathB))
        {
            Console.Error.WriteLine($"Right repo directory does not exist: {pathB}");
            return 1;
        }

        try
        {
            Console.WriteLine($"Opening left  repo: {pathA}");
            var repoA = await Repo.OpenAsync(pathA);

            Console.WriteLine($"Opening right repo: {pathB}");
            var repoB = await Repo.OpenAsync(pathB);

            var result = SemanticRepoComparer.Compare(repoA, pathA, repoB, pathB);
            
            if (result.SemanticallyIdentical)
            {
                Console.WriteLine();
                Console.WriteLine("SEMANTICALLY IDENTICAL");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("SEMANTIC DIFFERENCES FOUND:");
            Console.WriteLine();

            foreach (var line in result.Differences)
                Console.WriteLine(line);

            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error comparing repos:");
            Console.Error.WriteLine(ex);
            return 99;
        }
    }
}
