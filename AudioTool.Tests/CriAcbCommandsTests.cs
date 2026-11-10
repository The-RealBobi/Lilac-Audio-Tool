using System.Buffers.Binary;
using System.Security.Cryptography;
using Level5.AudioTool.Core.Cri;

namespace Level5.AudioTool.Tests;

public sealed class CriAcbCommandsTests
{
    [Fact]
    public void StreamPatchPreservesPayloadAndRecordsFinalAwbHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string acb = Path.Combine(directory, "sample.acb");
            string awb = Path.Combine(directory, "sample.awb");
            byte[] samples = "HCA\0test"u8.ToArray();
            byte[] bank = new Afs2Bank(2, 0, 4, 2, 32, 0, [new Afs2Entry(7, samples)]).Write();
            var hash = new UtfTable("StreamAwbHash", [[Field("Name", 0x5a, "sample"), Field("Hash", 0x5b, new byte[16])]]);
            var header = new UtfTable("StreamAwbAfs2Header", [[Field("Header", 0x5b, new byte[26])]]);
            var root = new UtfTable("Acb",
                [[Field("StreamAwbHash", 0x5b, hash.Write()), Field("StreamAwbAfs2Header", 0x5b, header.Write())]]);
            File.WriteAllBytes(acb, root.Write());
            File.WriteAllBytes(awb, bank);
            string[] arguments = ["patch-acb-stream-awb", acb, acb, "--awb", awb, "--provenance", "LAT_v1.1.0"];

            CriCommands.Run(arguments, directory, directory);

            byte[] patchedAcb = File.ReadAllBytes(acb);
            byte[] patchedAwb = File.ReadAllBytes(awb);
            var tables = UtfTable.Read(patchedAcb).NestedTables();
            Assert.True(patchedAwb.AsSpan().StartsWith(bank));
            Assert.Equal(samples, Assert.Single(Afs2Bank.Read(patchedAwb).Entries).Data);
            Assert.Equal((uint)(patchedAcb.Length - 8), BinaryPrimitives.ReadUInt32BigEndian(patchedAcb.AsSpan(4)));
            Assert.Equal(MD5.HashData(patchedAwb), Assert.IsType<byte[]>(tables["StreamAwbHash"].Rows[0][1].Value));
            Assert.Equal(patchedAwb[..26], Assert.IsType<byte[]>(tables["StreamAwbAfs2Header"].Rows[0][0].Value));

            CriCommands.Run(arguments, directory, directory);

            Assert.Equal(patchedAcb, File.ReadAllBytes(acb));
            Assert.Equal(patchedAwb, File.ReadAllBytes(awb));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static UtfField Field(string name, byte type, object value) => new(name, type, value, -1, 0);
}
