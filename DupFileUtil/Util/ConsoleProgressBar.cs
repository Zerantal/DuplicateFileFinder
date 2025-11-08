using System.Diagnostics;
using System.Text;
using DuplicateFileFinderLib.Util;

namespace DupFileUtil.Util;

internal class ConsoleProgressBar
{
    private const string Animation = @"|/-\";
    private const int UpdatePeriod = 125; // in ms

    private static int _animationIndex;
    private readonly Stopwatch _stopWatch = new();
    private string _currentProgressBarText = string.Empty; // progress bar text
    private bool _taskCommenced;

    public ConsoleProgressBar()
    {
        _stopWatch.Start();
    }

    public int BlockCount { get; set; } = 20;

    public string CompletionText { get; set; } = "done.";

    public void WriteProgressBar(double progress)
    {
        var progressBlockCount = (int)(progress * BlockCount);
        var percent = (int)(progress * 100);
        // ReSharper disable once UseStringInterpolation
        _currentProgressBarText = string.Format("[{0}{1}] {2,3}% {3}",
            new string('#', progressBlockCount), new string('-', BlockCount - progressBlockCount),
            percent,
            Animation[_animationIndex++ % Animation.Length]);

        Console.Write(_currentProgressBarText);
    }

    public void PrintProgress(DuplicateFileFinderProgressReport report)
    {
        if (report.StatusMessage != string.Empty || report.IsRunning)
        {
            if (_taskCommenced)
            {
                MoveToStartOfProgressBar();
                StringBuilder text = new();
                text.Append(CompletionText);
                text.Append(' ', Math.Max(_currentProgressBarText.Length - CompletionText.Length, 0));
                Console.WriteLine(text);
            }

            if (!report.IsRunning) Console.Write(report.StatusMessage + " ");

            _currentProgressBarText = string.Empty;
            _taskCommenced = true;
            return;
        }

        if (_stopWatch.ElapsedMilliseconds > UpdatePeriod)
        {
            _stopWatch.Restart();
            MoveToStartOfProgressBar();
            WriteProgressBar(report.PercentComplete);
        }
    }

    private void MoveToStartOfProgressBar()
    {
        for (var i = 0; i < _currentProgressBarText.Length; i++)
            Console.Write('\b');
    }
}