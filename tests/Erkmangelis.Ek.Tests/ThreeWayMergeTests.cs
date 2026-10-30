using System.Text;

namespace Erkmangelis.Ek.Tests;

public sealed class ThreeWayMergeTests
{
    private static readonly ThreeWayMerge.MergeText Merge = ThreeWayMerge.GitMergeFile("0.1.0", "0.5.0");

    private static Dictionary<string, GeneratedFile> Tree(params (string Path, string Content)[] files) =>
        files.ToDictionary(file => file.Path, file => new GeneratedFile(Encoding.UTF8.GetBytes(file.Content)));

    private static IReadOnlyList<FileChange> Plan(
        Dictionary<string, GeneratedFile> before, Dictionary<string, GeneratedFile> after, Dictionary<string, GeneratedFile> project) =>
        ThreeWayMerge.Plan(before, after, path => project.GetValueOrDefault(path)?.Content, Merge);

    private static string Text(FileChange change) => Encoding.UTF8.GetString(change.Content!);

    [Fact]
    public void A_file_the_project_never_changed_takes_the_new_version()
    {
        var change = Assert.Single(Plan(Tree(("a.txt", "one\n")), Tree(("a.txt", "two\n")), Tree(("a.txt", "one\n"))));

        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.Equal("two\n", Text(change));
    }

    [Fact]
    public void Files_the_template_did_not_change_are_left_alone()
    {
        Assert.Empty(Plan(Tree(("a.txt", "one\n")), Tree(("a.txt", "one\n")), Tree(("a.txt", "mine\n"))));
    }

    [Fact]
    public void Changes_on_different_lines_are_merged()
    {
        var before = Tree(("a.txt", "1\n2\n3\n4\n5\n6\n"));
        var after = Tree(("a.txt", "1\n2\n3\n4\n5\nsix\n"));
        var project = Tree(("a.txt", "one\n2\n3\n4\n5\n6\n"));

        var change = Assert.Single(Plan(before, after, project));

        Assert.Equal(ChangeKind.Merged, change.Kind);
        Assert.Equal("one\n2\n3\n4\n5\nsix\n", Text(change));
    }

    [Fact]
    public void Changes_to_the_same_line_get_conflict_markers_naming_both_sides()
    {
        var change = Assert.Single(Plan(Tree(("a.txt", "x\n")), Tree(("a.txt", "template\n")), Tree(("a.txt", "mine\n"))));

        Assert.Equal(ChangeKind.Conflict, change.Kind);
        Assert.Contains("<<<<<<< your project\nmine\n=======\ntemplate\n>>>>>>> template 0.5.0\n", Text(change));
    }

    [Fact]
    public void A_new_template_file_is_added()
    {
        var change = Assert.Single(Plan(Tree(), Tree(("docs/new.md", "new\n")), Tree()));

        Assert.Equal(ChangeKind.Added, change.Kind);
    }

    [Fact]
    public void A_file_the_project_already_has_in_the_new_version_is_left_alone()
    {
        Assert.Empty(Plan(Tree(("a.txt", "old\n")), Tree(("a.txt", "new\n")), Tree(("a.txt", "new\n"))));
    }

    [Fact]
    public void A_removed_template_file_goes_only_if_the_project_never_changed_it()
    {
        var unchanged = Assert.Single(Plan(Tree(("a.txt", "x\n")), Tree(), Tree(("a.txt", "x\n"))));
        var changed = Assert.Single(Plan(Tree(("a.txt", "x\n")), Tree(), Tree(("a.txt", "mine\n"))));

        Assert.Equal(ChangeKind.Removed, unchanged.Kind);
        Assert.Equal(ChangeKind.Kept, changed.Kind);
        Assert.Null(changed.Content);
    }

    [Fact]
    public void A_file_the_project_deleted_stays_deleted()
    {
        var change = Assert.Single(Plan(Tree(("a.txt", "x\n")), Tree(("a.txt", "y\n")), Tree()));

        Assert.Equal(ChangeKind.StaysDeleted, change.Kind);
        Assert.True(change.NeedsReview);
    }

    [Fact]
    public void A_new_template_file_where_the_project_has_its_own_is_a_conflict()
    {
        var change = Assert.Single(Plan(Tree(), Tree(("a.txt", "template\n")), Tree(("a.txt", "mine\n"))));

        Assert.Equal(ChangeKind.Conflict, change.Kind);
    }

    [Fact]
    public void Binary_files_both_sides_changed_are_kept_for_review()
    {
        var change = Assert.Single(Plan(Tree(("a.bin", "a\0")), Tree(("a.bin", "b\0")), Tree(("a.bin", "c\0"))));

        Assert.Equal(ChangeKind.Kept, change.Kind);
    }

    [Fact]
    public void Fresh_guids_are_aligned_with_the_project_before_comparing()
    {
        const string Project = "<UserSecretsId>Acme-11111111-1111-1111-1111-111111111111</UserSecretsId>\n";
        var before = Tree(("Api.csproj", "<UserSecretsId>Acme-22222222-2222-2222-2222-222222222222</UserSecretsId>\n"));
        var after = Tree(("Api.csproj", "<UserSecretsId>Acme-33333333-3333-3333-3333-333333333333</UserSecretsId>\n<Nullable>enable</Nullable>\n"));
        var project = Tree(("Api.csproj", Project));

        ThreeWayMerge.AlignGuids(before, after, path => project.GetValueOrDefault(path)?.Content);
        var change = Assert.Single(Plan(before, after, project));

        Assert.Equal(ChangeKind.Updated, change.Kind);
        Assert.Equal(Project + "<Nullable>enable</Nullable>\n", Text(change));
    }

    [Fact]
    public void Guids_that_are_the_same_in_both_versions_are_not_touched()
    {
        var before = Tree(("a.json", "\"id\": \"44444444-4444-4444-4444-444444444444\"\n"));
        var after = Tree(("a.json", "\"id\": \"44444444-4444-4444-4444-444444444444\"\n"));
        var project = Tree(("a.json", "\"id\": \"55555555-5555-5555-5555-555555555555\"\n"));

        ThreeWayMerge.AlignGuids(before, after, path => project.GetValueOrDefault(path)?.Content);

        Assert.Contains("44444444", Encoding.UTF8.GetString(before["a.json"].Content));
    }
}
