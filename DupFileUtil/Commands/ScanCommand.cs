using System.Diagnostics;
using CommandLine;
using DupFileUtil.Util;
using DuplicateFileFinderLib;

namespace DupFileUtil.Commands
{
    [Verb("scan", HelpText = "Scan directories and compute hash values")]
    internal class ScanCommand : ICommand
    {
        [Option('d', "directories", Min=1, Required = true, Separator = ',', HelpText = "Comma delimited list of directories")]
        public IEnumerable<string>? Directories { get; set; }

        [Option('o', "out", Required = true, HelpText = "output csv file")]
        public string? OutputFilename { get; set; }

        private readonly ConsoleProgressBar _progressBar = new();

        public void Execute()
        {
            //Start asynchronous message pump
            StandaloneSynchronizationContext.Start(ExecuteAsync);
        }

        public async Task ExecuteAsync()
        {
            DuplicateFileFinder dupFileFinder = new DuplicateFileFinder();
            Debug.Assert(Directories != null, nameof(Directories) + " != null");

            // scan
            _progressBar.BlockCount = 20;
            var progressIndicator = new Progress<DuplicateFileFinderProgressReport>(_progressBar.PrintProgress);
            foreach (var d in Directories) await dupFileFinder.ScanLocation(d, progressIndicator);

            // output csv file
            Debug.Assert(OutputFilename != null, nameof(OutputFilename) + " != null");
            await using StreamWriter writer = new StreamWriter(OutputFilename);
            try
            {
                dupFileFinder.ExportToCsv(writer);
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to write output file: " + e.Message);
            }
        }
    }
}
