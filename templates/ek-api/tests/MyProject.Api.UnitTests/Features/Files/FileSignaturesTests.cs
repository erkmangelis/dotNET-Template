namespace MyProject.Api.UnitTests.Features.Files;

public sealed class FileSignaturesTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "x.bin", FileSignatures.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "x", FileSignatures.Jpeg)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "x", FileSignatures.Gif)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "x", FileSignatures.Webp)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "x.png", FileSignatures.Pdf)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "book.xlsx", FileSignatures.Xlsx)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "book.xlsx.zip", FileSignatures.Zip)]
    public void The_signature_decides_the_type(byte[] header, string fileName, string expected)
    {
        Assert.Equal(expected, FileSignatures.Detect(header, fileName));
    }

    [Fact]
    public void Text_is_csv_or_plain_by_its_extension()
    {
        var text = "Başlık,İçerik\r\nşğü,ok\n"u8.ToArray();

        Assert.Equal(FileSignatures.Csv, FileSignatures.Detect(text, "notes.CSV"));
        Assert.Equal(FileSignatures.Text, FileSignatures.Detect(text, "notes.txt"));
    }

    [Fact]
    public void A_character_cut_off_by_the_header_still_counts_as_text()
    {
        var text = "abc ş"u8.ToArray();

        Assert.Equal(FileSignatures.Text, FileSignatures.Detect(text.AsSpan(0, text.Length - 1), "a.txt"));
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })]
    [InlineData(new byte[] { 0x61, 0x62, 0xC3, 0x28, 0x63 })]
    [InlineData(new byte[] { })]
    public void Binary_or_broken_content_has_no_type(byte[] header)
    {
        Assert.Null(FileSignatures.Detect(header, "readme.txt"));
    }
}
