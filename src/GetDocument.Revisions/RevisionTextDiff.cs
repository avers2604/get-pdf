using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace GetDocument.Revisions;

public enum RevisionLineKind { Unchanged, Inserted, Deleted, Modified }
public sealed record RevisionDiffLine(RevisionLineKind Kind, string Text);

/// <summary>
/// Line-level text comparison using DiffPlex 1.9.0 (Apache-2.0).
/// This does NOT compare DOCX formatting, PDF graphics, spreadsheet formulas or signatures.
/// </summary>
public static class RevisionTextDiff
{
    public const int MaxCharacters = 100_000;
    public const int MaxLines = 3000;

    public static IReadOnlyList<RevisionDiffLine> Compare(string original, string edited)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        if (original.Length > MaxCharacters || edited.Length > MaxCharacters)
            throw new ArgumentOutOfRangeException(nameof(original), "Тексты слишком велики для сравнения.");
        if (original.Count(c => c == '\n') > MaxLines ||
            edited.Count(c => c == '\n') > MaxLines)
            throw new ArgumentOutOfRangeException(nameof(original), "Слишком много строк.");
        if (original.Length == 0 && edited.Length == 0)
            return [];
        // Keep whitespace differences visible.
        var model = InlineDiffBuilder.Diff(original, edited, ignoreWhiteSpace: false);
        if (model.Lines.Count > MaxLines * 2)
            throw new InvalidDataException("Результат сравнения слишком велик.");
        return model.Lines.Select(line => new RevisionDiffLine(
            line.Type switch
            {
                ChangeType.Inserted => RevisionLineKind.Inserted,
                ChangeType.Deleted => RevisionLineKind.Deleted,
                ChangeType.Modified => RevisionLineKind.Modified,
                _ => RevisionLineKind.Unchanged
            }, line.Text ?? "")).ToArray();
    }
}
