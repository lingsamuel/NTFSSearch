namespace NtfsSearch.Core;

public sealed record JournalPosition(ulong JournalId, long NextUsn);
public sealed record IndexedLink(ulong ParentReference, string ParentPath, string Name);
public sealed record IndexedFileState(ulong FileReference, bool IsDirectory,
    IReadOnlyList<IndexedLink> Links, long? Size = null, long? ModifiedUtcTicks = null);
public sealed record LiveTransaction(JournalPosition From, JournalPosition To, IReadOnlyList<IndexedFileState> Files);
public sealed record CommittedChange(long Sequence, LiveTransaction Transaction);
