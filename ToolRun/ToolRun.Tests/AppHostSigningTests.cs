using Tool2App;
using Tool2App.Tests;
using Xunit;

namespace ToolRun.Tests;

public class AppHostSigningTests : IDisposable
{
    private readonly string _dir = TestFiles.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string File_(string name, byte[] bytes) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, bytes); return p; }

    [Theory]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0, 0 }, true)]    // 64-bit little endian (arm64/x64 thin)
    [InlineData(new byte[] { 0xCE, 0xFA, 0xED, 0xFE, 0, 0 }, true)]
    [InlineData(new byte[] { 0xFE, 0xED, 0xFA, 0xCF, 0, 0 }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0 }, true)]    // fat
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, false)]
    [InlineData(new byte[] { (byte)'M', (byte)'Z', 0, 0 }, false)]
    [InlineData(new byte[] { 1, 2 }, false)]
    public void RecognisesMachO(byte[] head, bool expected) => Assert.Equal(expected, AppHosts.IsMachO(File_("t", head)));

    [Fact]
    public void OnlyARealMachOTemplateOnAMacIsSigned()
    {
        var macho = File_("real", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0 });
        var fake = File_("fake", System.Text.Encoding.ASCII.GetBytes("APPHOST-STUB"));
        var mac = Rid.Parse("osx-arm64"); var linux = Rid.Parse("linux-x64"); var win = Rid.Parse("win-x64");
        Assert.True(AppHosts.ShouldSign(mac, macho, true));       // real apphost on a Mac stays signed: arm64 macOS will not run it otherwise
        Assert.False(AppHosts.ShouldSign(mac, fake, true));       // a stand-in is never signed (HostModel would throw "Cannot sign a non-Mach-O file")
        Assert.False(AppHosts.ShouldSign(mac, macho, false));     // not on a Mac: no signing tool
        Assert.False(AppHosts.ShouldSign(linux, macho, true));
        Assert.False(AppHosts.ShouldSign(win, fake, true));
    }

    [Fact]
    public async Task AFakeTemplateWorksOnEveryOs()
    {
        using var f = new Fixture();
        f.AddTool("Tiny.Tool", "1.0.0");
        var plan = await f.App(new[] { "tiny.tool" }, Array.Empty<string>()).PrepareAsync(false);   // would throw on a Mac before: fixtures hand HostModel a non-Mach-O template
        Assert.True(File.Exists(plan.FileName));
    }
}
