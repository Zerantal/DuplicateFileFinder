using System.Diagnostics;
using System.Text;
using DuplicateFileFinderLib;

namespace DupFileUtil.Util
{
    internal class ConsoleProgressBar
    {
        private string _currentProgressBarText = string.Empty; // progress bar text

        public int BlockCount { get; set; } = 20;
        private const string Animation = @"|/-\";
        private const int UpdatePeriod = 125;           // in ms

        private static int _animationIndex = 0;
        private readonly Stopwatch _stopWatch = new();

        public string CompletionText { get; set; } = "done.";
        private bool _taskCommenced;

        public ConsoleProgressBar()
        {
            _stopWatch.Start();
            
        }

        public void WriteProgressBar(double progress)
        {
            int progressBlockCount = (int)(progress * BlockCount);
            int percent = (int)(progress * 100);
            // ReSharper disable once UseStringInterpolation
            _currentProgressBarText = string.Format("[{0}{1}] {2,3}% {3}",
                new string('#', progressBlockCount), new string('-', BlockCount - progressBlockCount),
                percent,
                Animation[_animationIndex++ % Animation.Length]);

            Console.Write(_currentProgressBarText);
        }

        public void PrintProgress(DuplicateFileFinderProgressReport report)
        {
            if (report.CommencingNewTask || report.Finished)
            {
                if (_taskCommenced)
                {
                    MoveToStartOfProgressBar();
                    StringBuilder text = new StringBuilder();
                    text.Append(CompletionText);
                    text.Append(' ', Math.Max(_currentProgressBarText.Length - CompletionText.Length, 0));
                    Console.WriteLine(text);
                }

                if (!report.Finished)
                {
                    Console.Write(report.NewTask + " ");
                }

                _currentProgressBarText = string.Empty;
                _taskCommenced = true;
                return;
            }
            
            if (_stopWatch.ElapsedMilliseconds > UpdatePeriod)
            {
                _stopWatch.Restart();
                MoveToStartOfProgressBar();
                WriteProgressBar(report.CurrentProgress);
            }
        }

        private void MoveToStartOfProgressBar()
        {
            for (int i = 0; i < _currentProgressBarText.Length; i++)
                Console.Write('\b');
        }
    }
}
