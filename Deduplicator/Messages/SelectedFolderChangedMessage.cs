using DuplicateFileFinderLib;
using Microsoft.Toolkit.Mvvm.Messaging.Messages;

namespace DuplicateFileFinder.Messages;

internal class SelectedFolderChangedMessage : ValueChangedMessage<FolderNode>
{
    public SelectedFolderChangedMessage(FolderNode value) : base(value)
    {
    }
}