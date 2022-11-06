namespace DuplicateFileFinderLib
{

    // Notes: scenarios that need testing
    

    public class DuplicateFileFinderProgressReport
    {
        private readonly double _currentProgress;

        // current progress (between 0 and 1) of task
        public double CurrentProgress
        {
            get => _currentProgress;
            private init => _currentProgress = Math.Max(0, Math.Min(1, value));
        }

        public bool CommencingNewTask => !string.IsNullOrEmpty(NewTask);

        public bool Finished { get; }

        public string NewTask { get; } = string.Empty;

        public DuplicateFileFinderProgressReport(double progress)
        {
            CurrentProgress = progress;
        }

        public DuplicateFileFinderProgressReport(string task)
        {
            NewTask = task;
        }

        // empty constructor indicates end of progress updates
        public DuplicateFileFinderProgressReport()
        {
            Finished = true;
        }
    }
}
