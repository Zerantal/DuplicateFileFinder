namespace DuplicateFileFinder.Gui.Infrastructure.Status;

public sealed record StatusItem(string Key, string Value);

public interface IStatusProvider
{
    event EventHandler? StatusChanged;

    /// <summary>Return a snapshot of current status items.</summary>
    IReadOnlyList<StatusItem> GetStatusItems();
}
