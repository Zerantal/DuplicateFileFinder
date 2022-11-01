using System;
using DuplicateFileFinder.Common;

namespace DuplicateFileFinder.ViewModels;

public class ProgressDialogViewModel : BindableBase
{
    private double _progress;
    private string _currentTask;

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(value - _progress) < 0.01) return;
            _progress = value;
            RaisePropertyChanged();
        }
    }

    public string CurrentTask
    {
        get => _currentTask;
        set
        {
            if (value == _currentTask) return;
            _currentTask = value;
            RaisePropertyChanged();
        }
    }

}