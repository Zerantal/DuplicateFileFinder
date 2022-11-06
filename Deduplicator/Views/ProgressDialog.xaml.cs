using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Deduplicator.ViewModels;
using DuplicateFileFinderLib;

namespace Deduplicator.Views;

/// <summary>
/// Interaction logic for ProgressDialog.xaml
/// </summary>
public partial class ProgressDialog : Window
{
    public ProgressDialogViewModel ViewModel { get; }

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public ProgressDialog(Func<Progress<DuplicateFileFinderProgressReport>, CancellationToken, Task> longRunningTask)
    {
        ViewModel = new ProgressDialogViewModel();


        InitializeComponent();

        var progressIndicator = new Progress<DuplicateFileFinderProgressReport>(UpdateProgress);

        Task.Run(() => longRunningTask(progressIndicator, _cancellationTokenSource.Token)).ConfigureAwait(false);

    }

    private void UpdateProgress(DuplicateFileFinderProgressReport report)
    {
        if (report.Finished)
        {
            Close();
            return;
        }

        if (report.CommencingNewTask)
        {
            ViewModel.CurrentTask = report.NewTask;
            ViewModel.Progress = 0.0;
            return;
        }

        ViewModel.Progress = report.CurrentProgress;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource.Cancel();
        Close();
    }
}