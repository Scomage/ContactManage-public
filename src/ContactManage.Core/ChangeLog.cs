using System.Text;

namespace ContactManage.Core;

/// <param name="Destination">Where the result went, in words ("Replaced the contents of ...", "Saved to file ...").</param>
/// <param name="SourceFolders">The folders that were read, as "Account - Folder".</param>
/// <param name="FolderName">Turns a contact's <see cref="Contact.SourceFolderId"/> into "Account - Folder".</param>
/// <param name="ReplacedFolderIds">Folders whose contents were replaced; only there were originals actually changed or deleted.</param>
public sealed record ChangeLogContext(DateTime When, string Destination, IReadOnlyList<string> SourceFolders,
    Func<string?, string> FolderName, IReadOnlySet<string> ReplacedFolderIds);

/// <summary>
/// A plain-text account of one merge run: what happened to every contact. It holds names, emails and phone numbers,
/// so it is a file for the user, kept on their own PC, never part of the repository.
/// </summary>
public static class ChangeLog
{
    public static string Build(MergeResult result, ChangeLogContext ctx)
    {
        var sb = new StringBuilder();
        var summary = result.Summary;
        sb.AppendLine("ContactManage change log");
        sb.AppendLine($"When:         {ctx.When:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Folders read: {string.Join("; ", ctx.SourceFolders)}");
        sb.AppendLine($"Result:       {ctx.Destination}");
        sb.AppendLine($"Summary:      {summary.Text}");
        sb.AppendLine(ctx.ReplacedFolderIds.Count == 0
            ? "The folders that were read were not changed."
            : "Only folders that were replaced were changed; contacts living in other folders are as they were.");

        var groups = result.Groups;
        Section(sb, "MERGED", groups.Where(g => g.WillMerge && !g.HasUnresolvedConflicts), (g, n) =>
        {
            sb.AppendLine($"[{n}] {Title(g)}: {g.Members.Count} contacts merged into one");
            sb.AppendLine("    Combined from:");
            foreach (var m in g.Members) sb.AppendLine($"      - {Line(m, ctx)}");
            sb.AppendLine($"    Now:  {Line(g.BuildMerged(), ctx, withFolder: false)}");
            foreach (var c in g.Conflicts) sb.AppendLine($"    {Conflict(c)}");
            sb.AppendLine($"    {Fate(g, ctx, "merged away", "updated or created")}");
        });

        Section(sb, "KEPT SEPARATE (you decided these are different people)", groups.Where(g => g.Decision == GroupDecision.KeepSeparate), (g, n) =>
        {
            sb.AppendLine($"[{n}] {Title(g)}: {g.Members.Count} contacts left as they were{(g.Remembered ? " (remembered from an earlier run)" : "")}");
            foreach (var m in g.Members) sb.AppendLine($"      - {Line(m, ctx)}");
        });

        Section(sb, "REMOVED (you marked these obsolete)", groups.Where(g => g.WillRemove), (g, n) =>
        {
            sb.AppendLine($"[{n}] {Title(g)}: all {g.Members.Count} contacts removed");
            foreach (var m in g.Members) sb.AppendLine($"      - {Line(m, ctx)}");
            sb.AppendLine($"    {Fate(g, ctx, "deleted", "")}");
        });

        Section(sb, "LEFT UNDECIDED (unchanged)", groups.Where(g => g.Decision == GroupDecision.Pending || g.HasUnresolvedConflicts), (g, n) =>
        {
            sb.AppendLine($"[{n}] {Title(g)}: {g.Members.Count} contacts left as they were" +
                (g.HasUnresolvedConflicts ? " (marked to merge, but fields that differ were not settled)" : " (no decision)"));
            foreach (var m in g.Members) sb.AppendLine($"      - {Line(m, ctx)}");
        });

        Section(sb, "RETAINED (no duplicates found)", result.Unduplicated, (c, _) => sb.AppendLine($"  - {Line(c, ctx)}"), numbered: false);
        return sb.ToString();
    }

    private static void Section<T>(StringBuilder sb, string heading, IEnumerable<T> items, Action<T, int> write, bool numbered = true)
    {
        var list = items.ToList();
        sb.AppendLine().AppendLine($"== {heading}: {list.Count} ==");
        for (var i = 0; i < list.Count; i++)
        {
            write(list[i], i + 1);
            if (numbered) sb.AppendLine();
        }
    }

    private static string Title(MergeGroup g) => Name(g.Members[0]);

    private static string Name(Contact c) =>
        !string.IsNullOrWhiteSpace(c.DisplayName) ? c.DisplayName.Trim()
        : $"{c.GivenName} {c.FamilyName}".Trim() is { Length: > 0 } n ? n
        : c.Emails.FirstOrDefault() ?? c.Company ?? "(no name)";

    private static string Line(Contact c, ChangeLogContext ctx, bool withFolder = true)
    {
        var parts = new List<string> { Name(c) };
        if (c.Emails.Count > 0) parts.Add(string.Join(", ", c.Emails));
        if (c.Phones.Count > 0) parts.Add(string.Join(", ", c.Phones));
        if (!string.IsNullOrWhiteSpace(c.Company)) parts.Add(c.Company.Trim());
        if (c.Groups.Count > 0) parts.Add("labels: " + string.Join(", ", c.Groups));
        if (withFolder && c.SourceFolderId is not null) parts.Add("in " + ctx.FolderName(c.SourceFolderId));
        return string.Join(" | ", parts);
    }

    /// <summary>What became of the originals: only those in a replaced folder were actually changed on the server.</summary>
    private static string Fate(MergeGroup g, ChangeLogContext ctx, string inReplaced, string unused)
    {
        var changed = g.Members.Count(m => m.SourceFolderId is not null && ctx.ReplacedFolderIds.Contains(m.SourceFolderId));
        if (ctx.ReplacedFolderIds.Count == 0) return "The originals are still in their folders.";
        return changed == g.Members.Count ? $"All {changed} originals were {inReplaced} in the replaced folders."
            : changed == 0 ? "The originals are in folders that were not replaced, so they are still there."
            : $"{changed} of {g.Members.Count} originals ({inReplaced}) were in replaced folders; the rest are in folders that were not replaced and are still there.";
    }

    private static string Conflict(FieldConflict c)
    {
        var field = c.Field switch { ContactField.DisplayName => "Name", ContactField.GivenName => "First name", ContactField.FamilyName => "Last name", ContactField.JobTitle => "Job title", var f => f.ToString() };
        var all = string.Join(" / ", c.Options.Select(o => $"\"{o.Value}\""));
        return c.Resolution switch
        {
            ResolutionKind.Chosen => $"{field} differed ({all}): kept \"{c.ResolvedValue}\"",
            ResolutionKind.KeepBoth => $"{field} differed ({all}): kept both",
            ResolutionKind.Custom => $"{field} differed ({all}): you typed \"{c.ResolvedValue}\"",
            ResolutionKind.Blank => $"{field} differed ({all}): left blank",
            _ => $"{field} differed ({all}): not settled",
        };
    }
}
