using Level5.AudioTool.Core.Cri;

namespace Level5.AudioTool.Tests;

public class CriUtfTests
{
    // Captured from Python serialize_utf for the table constructed below.
    private static readonly byte[] Reference = Convert.FromBase64String(
        "QFVURgAAAJ4AAAA2AAAAcgAAAJsAAAAAAAYAHgAAAAJUAAAADFMAAAAPWgAAABZbAAAAG1gAAAAgVgAAACUAAAAR//4AAAAIAAAAAAAAAAM/wAAA/ty6mHZUMhAAAAAR//8AAAAIAAAAAwAAAAAAAAAAAAAAAAAAAAlTYW1wbGVzAOmfswBJRABTaWduZWQATmFtZQBEYXRhAFJhdGUAQmlnAAMBBA==");

    [Fact]
    public void WriterMatchesPythonWithExpandedConstantsAndDeduplicatedStrings()
    {
        var table = new UtfTable("Samples",
        [
            [Field("ID", 0x34, 17), Field("Signed", 0x53, -2), Field("Name", 0x5a, "音"),
             Field("Data", 0x5b, new byte[] { 3, 1, 4 }), Field("Rate", 0x58, 1.5f), Field("Big", 0x56, 0xfedcba9876543210UL)],
            [Field("ID", 0x34, 17), Field("Signed", 0x53, -1), Field("Name", 0x5a, "音"),
             Field("Data", 0x5b, Array.Empty<byte>()), Field("Rate", 0x58, -0f), Field("Big", 0x56, 9)]
        ]);

        Assert.Equal(Reference, table.Write());
    }

    [Fact]
    public void ReaderFindsNestedDataAndReportsAbsoluteOffsets()
    {
        var parent = new UtfTable("Parent", [[Field("Samples", 0x5b, Reference)]]);
        var parsed = UtfTable.Read(parent.Write());
        var childOffset = parsed.Rows[0][0].Offset;
        var child = parsed.NestedTables()["Samples"];

        Assert.Equal("Samples", child.Name);
        Assert.Equal(17, UtfField.Integer(child.Rows[0], "id"));
        Assert.Equal(-2, UtfField.Integer(child.Rows[0], "Signed"));
        Assert.Equal("音", child.Rows[0][2].Value);
        Assert.Equal(0xfedcba9876543210UL, child.Rows[0][5].Value);
        Assert.Equal(childOffset + 163, child.Rows[0][3].Offset);
        Assert.Equal(Reference, child.Write());
    }

    private static UtfField Field(string name, byte type, object value) => new(name, type, value, -1, 0);
}
