using System.Collections.ObjectModel;
using System.Linq;
using Deduplicator.Messages;
using Deduplicator.Models;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Mvvm.Messaging;

namespace Deduplicator.ViewModels;

public class FolderViewModel : ObservableRecipient
{
    public ObservableCollection<FolderModel> RootFolders { get; }

    public RelayCommand<FolderModel> FolderSelectionChanged { get; }


    public FolderViewModel()
    {
        RootFolders = new ObservableCollection<FolderModel>();
        IsActive = true;

        FolderSelectionChanged = new RelayCommand<FolderModel>(UpdateFileList, _ => RootFolders != null);
    }

    private void UpdateFileList(FolderModel folder)
    {
        if (folder == null) return;

        Messenger.Send(new SelectedFolderChangedMessage(folder.Folder));
    }

    protected override void OnActivated()
    {
        Messenger.Register<FolderViewModel, DuplicateFileListChangedMessage>(this, (_, m) =>
        {
            RootFolders.Clear();
            foreach (var f in m.Value.SubFoldersContainingDuplicates.Select(n => new FolderModel(n)))
            {
                RootFolders.Add(f);
            }
        });
    }

    //public void FolderTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    //{
    //    FolderModel folder = e.NewValue as FolderModel;
    //    Messenger.Send(new SelectedFolderChangedMessage(folder.Folder));
    //}
}