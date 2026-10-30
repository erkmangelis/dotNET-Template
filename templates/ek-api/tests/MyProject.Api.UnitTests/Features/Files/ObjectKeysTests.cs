namespace MyProject.Api.UnitTests.Features.Files;

public sealed class ObjectKeysTests
{
    [Fact]
    public void Keys_lead_back_to_their_file()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id, ObjectKeys.FileId(ObjectKeys.Content(id)));
        Assert.Equal(id, ObjectKeys.FileId(ObjectKeys.Thumbnail(id)));
    }

    [Theory]
    [InlineData("files/not-a-guid")]
    [InlineData("other/0198c2a1b3d47e8f9a0b1c2d3e4f5a6b")]
    public void A_key_this_app_did_not_make_has_no_file(string key)
    {
        Assert.Null(ObjectKeys.FileId(key));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\report.pdf", "report.pdf")]
    [InlineData("bad\u0000name\n.txt", "badname.txt")]
    [InlineData("   ", "file")]
    [InlineData(null, "file")]
    public void File_names_are_only_ever_a_name(string? name, string expected)
    {
        Assert.Equal(expected, FileEndpoints.SafeFileName(name));
    }
}
