using DuplicateFileFinderLib;
using Microsoft.Toolkit.Mvvm.Messaging.Messages;

namespace Deduplicator.Messages;

internal class SelectedFolderChangedMessage : ValueChangedMessage<FolderNode>
{
    public SelectedFolderChangedMessage(FolderNode value) : base(value)
    {
    }
}