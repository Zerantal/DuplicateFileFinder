using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Deduplicator.Messages;
using Deduplicator.Models;
using DuplicateFileFinderLib;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Messaging;

namespace Deduplicator.ViewModels;

public class FilesViewModel : ObservableRecipient
{
    public ObservableCollection<FileModel> DuplicateFiles { get; }

    private readonly HashSet<FileGroup> _fileGroups;

    public FilesViewModel()
    {
        DuplicateFiles = new ObservableCollection<FileModel>();
        _fileGroups = new HashSet<FileGroup>();

        IsActive = true;
    }

    protected override void OnActivated()
    {
        Messenger.Register<FilesViewModel, SelectedFolderChangedMessage>(this, (_, m) =>
        {
            _fileGroups.Clear();

            m.Value.TraverseFolders(f =>
            {
                foreach (var file in f.Files.Where(file => file.IsDuplicated))
                    _fileGroups.Add((FileGroup)file.Group);
                return Task.CompletedTask;
            }).Wait();

            DuplicateFiles.Clear();
            foreach (var f in _fileGroups.SelectMany(g => g.Files))
                DuplicateFiles.Add(new FileModel(f));
        });
    }

}