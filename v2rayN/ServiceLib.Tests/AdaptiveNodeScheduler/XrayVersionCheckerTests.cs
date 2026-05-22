using AwesomeAssertions;
using ServiceLib.Handler.AdaptiveNodeScheduler;
using Xunit;

namespace ServiceLib.Tests.AdaptiveNodeScheduler;

/// <summary>
/// P1.5: Verifies xray version parsing and minimum version compatibility check.
/// </summary>
public class XrayVersionCheckerTests
{
    [Fact]
    public void ParseVersion_XrayStandardFormat_ShouldParse()
    {
        var output = "Xray 1.8.1 (Xray, Penetrates Everything.) Custom (go1.21.4 windows/amd64)";
        var version = XrayVersionChecker.ParseVersion(output);
        version.Should().NotBeNull();
        version!.Major.Should().Be(1);
        version.Minor.Should().Be(8);
        version.Build.Should().Be(1);
    }

    [Fact]
    public void ParseVersion_XrayWithVPrefix_ShouldParse()
    {
        var output = "Xray v26.3.27";
        var version = XrayVersionChecker.ParseVersion(output);
        version.Should().NotBeNull();
        version!.Should().Be(new Version(26, 3, 27));
    }

    [Fact]
    public void ParseVersion_XrayWithoutVPrefix_ShouldParse()
    {
        var output = "Xray 26.3.27";
        var version = XrayVersionChecker.ParseVersion(output);
        version.Should().NotBeNull();
        version!.Should().Be(new Version(26, 3, 27));
    }

    [Fact]
    public void ParseVersion_XrayCoreFormat_ShouldParse()
    {
        // Another common output format
        var output = "Xray 24.12.14 (Xray, Penetrates Everything.)";
        var version = XrayVersionChecker.ParseVersion(output);
        version.Should().NotBeNull();
        version!.Major.Should().Be(24);
    }

    [Fact]
    public void ParseVersion_NullOrEmpty_ShouldReturnNull()
    {
        XrayVersionChecker.ParseVersion(null!).Should().BeNull();
        XrayVersionChecker.ParseVersion("").Should().BeNull();
        XrayVersionChecker.ParseVersion("   ").Should().BeNull();
    }

    [Fact]
    public void ParseVersion_UnrelatedText_ShouldReturnNull()
    {
        // Output from a completely different program
        XrayVersionChecker.ParseVersion("nginx version: nginx/1.18.0").Should().BeNull();
        XrayVersionChecker.ParseVersion("Random text without xray").Should().BeNull();
    }

    [Fact]
    public void IsCompatible_VerifiedVersion_ShouldBeTrue()
    {
        XrayVersionChecker.IsCompatible("Xray v26.3.27").Should().BeTrue();
    }

    [Fact]
    public void IsCompatible_NewerVersion_ShouldBeTrue()
    {
        XrayVersionChecker.IsCompatible("Xray v30.0.0").Should().BeTrue();
        XrayVersionChecker.IsCompatible("Xray v26.4.0").Should().BeTrue();
    }

    [Fact]
    public void IsCompatible_OlderVersion_ShouldBeFalse()
    {
        // Below minimum v26.3.27
        XrayVersionChecker.IsCompatible("Xray v1.8.1").Should().BeFalse();
        XrayVersionChecker.IsCompatible("Xray v25.0.0").Should().BeFalse();
    }

    [Fact]
    public void IsCompatible_UnparseableVersion_ShouldBeNull()
    {
        XrayVersionChecker.IsCompatible("").Should().BeNull();
        XrayVersionChecker.IsCompatible("garbage").Should().BeNull();
    }

    [Fact]
    public void GetCompatibilityMessage_Compatible_ShouldIndicateOk()
    {
        var msg = XrayVersionChecker.GetCompatibilityMessage("Xray v26.3.27");
        msg.Should().Contain("compatible");
    }

    [Fact]
    public void GetCompatibilityMessage_Incompatible_ShouldMentionUpgrade()
    {
        var msg = XrayVersionChecker.GetCompatibilityMessage("Xray v1.8.1");
        msg.Should().Contain("below minimum");
        msg.Should().Contain("upgrade");
    }

    [Fact]
    public void GetCompatibilityMessage_Unknown_ShouldIndicateError()
    {
        var msg = XrayVersionChecker.GetCompatibilityMessage("");
        msg.Should().Contain("Could not parse");
    }

    [Fact]
    public void ParseVersion_ExactMinimumVersion_ShouldBeParsedCorrectly()
    {
        // The minimum version itself should parse correctly
        var minVer = XrayVersionChecker.MinVersion;
        minVer.Should().Be(new Version(26, 3, 27));

        var version = XrayVersionChecker.ParseVersion("Xray v26.3.27");
        version.Should().Be(minVer);
    }

    [Fact]
    public void ParseVersion_LowercaseXray_ShouldParse()
    {
        var output = "xray 1.8.1";
        var version = XrayVersionChecker.ParseVersion(output);
        version.Should().NotBeNull();
        version!.Should().Be(new Version(1, 8, 1));
    }
}
