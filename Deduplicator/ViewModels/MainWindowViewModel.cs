#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DuplicateFileFinder.Command;
using DuplicateFileFinder.Messages;
using DuplicateFileFinder.Views;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Messaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DuplicateFileFinder.ViewModels;

internal class MainWindowViewModel : ObservableRecipient
{
    private int _duplicateFiles;

    private int _filesScanned;

    private string? _saveFilename;

    private long _spaceTaken;

    public MainWindowViewModel()
    {
        ClearCommand = new RelayCommand(_ => Clear(), _ => AreScansPresent);
        SaveCommand = new RelayCommand(_ => SaveScans(), _ => _saveFilename != null);
        SaveAsCommand = new RelayCommand(_ => SaveScansAs(), _ => AreScansPresent);
        ImportCommand = new RelayCommand(_ => ImportScans(), _ => true);
        ExitCommand = new RelayCommand(_ => Exit(), _ => true);
        ScanLocationCommand = new RelayCommand(_ => ScanLocation(), _ => true);
    }

    public RelayCommand ClearCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand SaveAsCommand { get; }

    public RelayCommand ImportCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand ScanLocationCommand { get; }

    public bool AreScansPresent => DuplicateFileFinder.LocationCount != 0;

    public int FilesScanned
    {
        get => _filesScanned;
        set => SetProperty(ref _filesScanned, value);
    }

    public int DuplicateFiles
    {
        get => _duplicateFiles;
        set => SetProperty(ref _duplicateFiles, value);
    }

    public long SpaceTaken
    {
        get => _spaceTaken;
        set => SetProperty(ref _spaceTaken, value);
    }

    public DuplicateFileFinderLib.DuplicateFileFinder DuplicateFileFinder { get; private set; } = new();

    private void ImportScans()
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            Title = "Import prior Deduplicator scans..."
        };
        if (openDialog.ShowDialog() != true) return;
        StreamReader? sr = null;
        var filename = openDialog.FileName;
        try
        {
            sr = new StreamReader(filename);
            DuplicateFileFinder.ImportFromCsv(sr);
            Messenger.Send(new DuplicateFileListChangedMessage(DuplicateFileFinder.root));

            UpdateStatusProperties();
        }
        catch (Exception e)
        {
            MessageBox.Show("Unable to import scan file: " + filename + "\n\n" + e.Message, "Error importing file",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            sr?.Close();
        }
    }

    private void UpdateStatusProperties()
    {
        FilesScanned = DuplicateFileFinder.root.AggregateFileCount;
        DuplicateFiles = DuplicateFileFinder.DuplicateFileCount;
        SpaceTaken = DuplicateFileFinder.SpaceTakenByDuplicates;
    }

    private void SaveScansAs()
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv"
        };
        if (saveDialog.ShowDialog() != true) return;
        _saveFilename = saveDialog.FileName;
        SaveScans();
    }

    private void SaveScans()
    {
        StreamWriter? sw = null;
        try
        {
            Debug.Assert(_saveFilename != null, nameof(_saveFilename) + " != null");
            sw = new StreamWriter(_saveFilename);
            DuplicateFileFinder.ExportToCsv(sw);
        }
        catch (Exception e)
        {
            MessageBox.Show("Unable to write to file: " + _saveFilename + "\n\n" + e.Message, "Error saving file",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            sw?.Close();
        }
    }

    private void Clear()
    {
        DuplicateFileFinder = new DuplicateFileFinderLib.DuplicateFileFinder();
        Messenger.Send(new DuplicateFileListChangedMessage(DuplicateFileFinder.root));
    }

    private static void Exit()
    {
        Application.Current.Shutdown();
    }


    private void ScanLocation()
    {
        var selectFolderDialog = new WinForms.FolderBrowserDialog();
        var result = selectFolderDialog.ShowDialog();
        if (result == WinForms.DialogResult.OK)
        {
            var progressDialog = new ProgressDialog((p, t) =>
                DuplicateFileFinder.ScanLocation(selectFolderDialog.SelectedPath, p, false, t));
            progressDialog.ShowDialog();

            UpdateStatusProperties();
        }

        // send message to notify FileTreeView
        Messenger.Send(new DuplicateFileListChangedMessage(DuplicateFileFinder.root));
    }
}