namespace DuplicateFileFinderLib.Scan;

public interface IEntryEnumerator
{
    IEnumerable<ScanEntry> EnumerateChildren(string dir, CancellationToken token);
}