using PCEssentials.Services.Launcher;
using Xunit;

namespace PCEssentials.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("vscode", "Visual Studio Code")]
    [InlineData("word", "Microsoft Word")]
    [InlineData("cafe", "Café Finder")]
    [InlineData("term", "Terminal")]
    [InlineData("ntpd", "Notepad")]
    [InlineData("studio code", "Visual Studio Code")]
    public void Matches(string query, string name) => Assert.True(FuzzyMatcher.Score(query, name) > 0);

    [Theory]
    [InlineData("xyz", "Notepad")]
    [InlineData("dapeton", "Notepad")]
    [InlineData("code zebra", "Visual Studio Code")]
    public void DoesNotMatch(string query, string name) => Assert.Equal(0, FuzzyMatcher.Score(query, name));

    [Theory]
    // Start of the name > start of a word > initials > inside a word > scattered letters.
    [InlineData("note", "Notepad", "OneNote")]
    [InlineData("code", "Code Writer", "Visual Studio Code")]
    [InlineData("vsc", "Visual Studio Code", "Avast Secure")]
    [InlineData("calc", "Calculator", "Excel Calculations Helper")]
    [InlineData("pad", "Pad Studio", "Notepad")]
    [InlineData("paint", "Paint", "Paint 3D")]
    public void RanksBetterMatchFirst(string query, string better, string worse) =>
        Assert.True(FuzzyMatcher.Score(query, better) > FuzzyMatcher.Score(query, worse),
            $"{better}={FuzzyMatcher.Score(query, better)} {worse}={FuzzyMatcher.Score(query, worse)}");
}

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("= 2 + 3 * 4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^10", 1024)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("10 % 4", 2)]
    [InlineData("sqrt(256)", 16)]
    [InlineData("2pi", 2 * Math.PI)]
    [InlineData("3(4+1)", 15)]
    [InlineData("1,234 * 2", 2468)]
    [InlineData("max(1,2,3)", 3)]
    [InlineData("3 x 4", 12)]
    [InlineData("12 ÷ 4", 3)]
    [InlineData("1e3 + 1", 1001)]
    [InlineData("log(1000)", 3)]
    [InlineData("abs(-5)", 5)]
    public void Evaluates(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var v), text);
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("e")]
    [InlineData("pi")]
    [InlineData("max")]
    [InlineData("2+")]
    [InlineData("(2+3")]
    [InlineData("foo(2)")]
    [InlineData("C++ Builder")]
    [InlineData("Paint 3D")]
    [InlineData("-")]
    public void IgnoresNonMaths(string text) => Assert.False(Calculator.TryEvaluate(text, out _));

    [Theory]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(1234567.5, "1,234,567.5")]
    [InlineData(1.0 / 3, "0.3333333333")]
    public void FormatsCleanly(double v, string expected) => Assert.Equal(expected, Calculator.Format(v));
}
