namespace PopTranslate.Core.Models;

public sealed record DiffSegment(
    DiffOperation Operation,
    string Text);
