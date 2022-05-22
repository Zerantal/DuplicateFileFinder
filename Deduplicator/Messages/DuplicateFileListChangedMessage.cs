using DuplicateFileFinderLib;
using Microsoft.Toolkit.Mvvm.Messaging.Messages;

namespace DuplicateFileFinder.Messages;

internal sealed class DuplicateFileListChangedMessage : ValueChangedMessage<RootNode>
{
    public DuplicateFileListChangedMessage(RootNode value) : base(value)
    {
    }
}