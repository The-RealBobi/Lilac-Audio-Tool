using Level5.AudioTool.Core.Cri;

namespace AudioTool.Tests;

public sealed class Afs2ParityTests
{
    [Fact]
    public void ReplacingAnEntryPreservesThePythonAlignmentAndTableLayout()
    {
        var original = Convert.FromHexString("4146533202020200020000002000120007000c001a002400480000000000000048434100000000000000000000000000000000000000000000000000000000005249464661626364");
        var bank = Afs2Bank.Read(original);
        Assert.Equal(original, bank.Write());
        bank.Entries[0] = bank.Entries[0] with { Data = new byte[66_560] };
        var replaced = bank.Write();
        Assert.Equal(4, replaced[5]);
        Assert.Equal(new byte[66_560], Afs2Bank.Read(replaced).Entries[0].Data);
        Assert.Equal("RIFFabcd"u8.ToArray(), Afs2Bank.Read(replaced).Entries[1].Data);
    }
    [Fact]
    public void FilenameEncryptedBankCanBeRebuiltAsThePythonReadableCopy()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "bank.awb");
        string output = Path.Combine(temp.Path, "output.awb");
        File.WriteAllBytes(source, Convert.FromHexString("8ae027e232f72eb60b43d81bd110927d387e3741e02d7927fe9b8d8a05c8d5ec3b15dbf2"));
        CriCommands.Run(["replace-awb", source, output], temp.Path, temp.Path);
        Assert.Equal(Convert.FromHexString("414653320202020001000000200012000700160024000000000000000000000048434100"),
            File.ReadAllBytes(output));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
