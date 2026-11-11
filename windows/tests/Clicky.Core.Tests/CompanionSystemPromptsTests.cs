using System.Text.RegularExpressions;
using Clicky.Core.Prompts;

namespace Clicky.Core.Tests;

/// <summary>
/// Guards the "port the prompts verbatim" rule by comparing the C# strings with the
/// Swift source in this same repository.
/// </summary>
public sealed class CompanionSystemPromptsTests
{
    private const char EmDashCharacter = '—';

    [Fact]
    public void CompanionVoicePromptMatchesSwiftSourceExactly()
    {
        string swiftPromptText = ReadSwiftMultilineStringConstant("companionVoiceResponseSystemPrompt");

        Assert.Equal(swiftPromptText, CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt);
    }

    [Fact]
    public void OnboardingDemoPromptMatchesSwiftSourceExactly()
    {
        string swiftPromptText = ReadSwiftMultilineStringConstant("onboardingDemoSystemPrompt");

        Assert.Equal(swiftPromptText, CompanionSystemPrompts.OnboardingDemoSystemPrompt);
    }

    [Fact]
    public void PromptsKeepTheirEmDashesAndUseLineFeedsOnly()
    {
        // The em dashes are part of the verbatim Mac prompt. They are written as an escape in
        // source so the repository grep for the literal character stays clean; this test stops
        // anyone from "fixing" them away.
        Assert.Contains(EmDashCharacter, CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt);
        Assert.Contains(EmDashCharacter, CompanionSystemPrompts.OnboardingDemoSystemPrompt);
        Assert.DoesNotContain('\r', CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt);
        Assert.DoesNotContain('\r', CompanionSystemPrompts.OnboardingDemoSystemPrompt);
        Assert.StartsWith("you're clicky, a friendly always-on companion", CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt);
        Assert.EndsWith("[POINT:400,300:terminal:screen2]\"", CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt);
    }

    [Fact]
    public void OnboardingDemoUserPromptMatchesSwiftSource()
    {
        string swiftSourceText = File.ReadAllText(FindSwiftCompanionManagerPath());

        Assert.Contains($"userPrompt: \"{CompanionSystemPrompts.OnboardingDemoUserPrompt}\"", swiftSourceText);
    }

    /// <summary>
    /// Extracts a Swift """ multi-line string constant and applies Swift's rule of removing
    /// the closing delimiter's indentation (four spaces here) from every line.
    /// </summary>
    private static string ReadSwiftMultilineStringConstant(string swiftConstantName)
    {
        string swiftSourceText = File.ReadAllText(FindSwiftCompanionManagerPath()).ReplaceLineEndings("\n");
        Match constantMatch = Regex.Match(
            swiftSourceText,
            "static let " + swiftConstantName + " = \"\"\"\n(.*?)\n    \"\"\"",
            RegexOptions.Singleline);

        Assert.True(constantMatch.Success, $"Could not find {swiftConstantName} in CompanionManager.swift.");

        IEnumerable<string> dedentedLines = constantMatch.Groups[1].Value
            .Split('\n')
            .Select(swiftLine => swiftLine.StartsWith("    ", StringComparison.Ordinal) ? swiftLine[4..] : swiftLine);

        return string.Join('\n', dedentedLines);
    }

    private static string FindSwiftCompanionManagerPath()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            string candidateSwiftFilePath = Path.Combine(currentDirectory.FullName, "leanring-buddy", "CompanionManager.swift");
            if (File.Exists(candidateSwiftFilePath))
            {
                return candidateSwiftFilePath;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new FileNotFoundException("Could not find leanring-buddy/CompanionManager.swift above the test output folder.");
    }
}
