using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

public readonly record struct ScanOptions(
    bool StartFresh = false,
    HashPolicyMode HashPolicy = HashPolicyMode.Default );