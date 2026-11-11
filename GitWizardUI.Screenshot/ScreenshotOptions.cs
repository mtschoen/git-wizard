namespace GitWizardUI.Screenshot;

public sealed record ScreenshotOptions(string OutputPath, string? StepsPath)
{
    public static ScreenshotOptions Parse(string[] arguments)
    {
        string? outputPath = null;
        string? stepsPath = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument == "--steps")
            {
                if (stepsPath != null || index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("--steps requires exactly one steps-file path.");
                }
                stepsPath = arguments[++index];
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal) || outputPath != null)
            {
                throw new ArgumentException("Usage: GitWizardUI.Screenshot [output.png] [--steps steps.json]");
            }
            else
            {
                outputPath = argument;
            }
        }
        return new ScreenshotOptions(outputPath ?? Path.Combine("Screenshots", "GitWizardUI.png"), stepsPath);
    }
}
