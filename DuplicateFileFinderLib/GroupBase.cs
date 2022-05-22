namespace DuplicateFileFinderLib;

public abstract class GroupBase
{
    private static int _idCounter;

    public int Id { get; }

    public abstract int DuplicateCount { get; }

    protected GroupBase()
    {
        Id = _idCounter;
        _idCounter++;
    }

    public override bool Equals(object? obj)
    {
        return obj is GroupBase grp && Equals(grp);
    }

    protected bool Equals(GroupBase other)
    {
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id;
    }
}