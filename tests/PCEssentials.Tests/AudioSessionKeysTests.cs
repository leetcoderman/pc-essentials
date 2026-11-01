using PCEssentials.Services;
using Xunit;

namespace PCEssentials.Tests;

public class AudioSessionKeysTests
{
    private const string Chrome = @"{0.0.0.00000000}.{d4ed536b-6315-4b07-b16c-87a2d67a90f1}|\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe%b{00000000-0000-0000-0000-000000000000}";
    private const string ChromeOnHeadset = @"{0.0.0.00000000}.{6103a4e4-62d2-4f0a-8cbe-1a9ca75fad05}|\Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe%b{00000000-0000-0000-0000-000000000000}";
    private const string SystemSounds = @"{0.0.0.00000000}.{d4ed536b-6315-4b07-b16c-87a2d67a90f1}|#%b{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}";

    [Fact]
    public void ExeComesFromTheIdentifier()
    {
        Assert.Equal("chrome.exe", AudioSessionKeys.ExeFromIdentifier(Chrome));
        Assert.Null(AudioSessionKeys.ExeFromIdentifier(SystemSounds));
        Assert.Null(AudioSessionKeys.ExeFromIdentifier(null));
        Assert.Null(AudioSessionKeys.ExeFromIdentifier("no bar here"));
    }

    [Fact]
    public void SessionsOfOneAppOnDifferentDevicesShareAKey()
    {
        Assert.Equal("chrome", AudioSessionKeys.Key(false, Chrome, "chrome"));
        Assert.Equal(AudioSessionKeys.Key(false, Chrome, null), AudioSessionKeys.Key(false, ChromeOnHeadset, null));
    }

    [Fact]
    public void KeyFallsBackToTheProcessName()
    {
        Assert.Equal("discord", AudioSessionKeys.Key(false, null, "Discord"));
        Assert.Null(AudioSessionKeys.Key(false, null, null));
        Assert.Equal(AudioSessionKeys.System, AudioSessionKeys.Key(true, SystemSounds, null));
    }

    [Fact]
    public void DisplayNamePrefersTheSessionNameThenTheDescription()
    {
        Assert.Equal("Game", AudioSessionKeys.DisplayName("Game", "Engine", "game", null));
        Assert.Equal("Google Chrome", AudioSessionKeys.DisplayName(@"@%SystemRoot%\System32\AudioSrv.Dll,-202", "Google Chrome", "chrome", Chrome));
        Assert.Equal("chrome", AudioSessionKeys.DisplayName(null, null, "chrome", Chrome));
        Assert.Equal("chrome", AudioSessionKeys.DisplayName(null, null, null, Chrome));
        Assert.Equal("Unknown app", AudioSessionKeys.DisplayName(null, null, null, null));
    }

    [Fact]
    public void SignatureChangesWhenSessionsOrActivityChange()
    {
        var a = AudioSessionKeys.Signature([("chrome", 1, false), ("system", 1, false)]);
        Assert.Equal(a, AudioSessionKeys.Signature([("chrome", 1, false), ("system", 1, false)]));
        Assert.NotEqual(a, AudioSessionKeys.Signature([("chrome", 2, false), ("system", 1, false)]));
        Assert.NotEqual(a, AudioSessionKeys.Signature([("chrome", 1, true), ("system", 1, false)]));
    }
}
