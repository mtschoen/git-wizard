using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GitWizardUI.Screenshot;

public static class ScreenshotSteps
{
    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyList<ScreenshotStep> Load(string path)
    {
        using var stream = File.OpenRead(path);
        var steps = JsonSerializer.Deserialize<ScreenshotStep?[]>(stream, SerializerOptions)
            ?? throw new JsonException("Steps file must contain a JSON array.");
        var result = new List<ScreenshotStep>(steps.Length);
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            if (step == null || !IsValid(step))
            {
                throw new JsonException($"Invalid screenshot step {index + 1}: expected setText (name or watermark, text), click (name), or capture (safe name).");
            }
            result.Add(step);
        }
        return result;
    }

    static bool IsValid(ScreenshotStep step)
    {
        var hasName = !string.IsNullOrWhiteSpace(step.Name);
        var hasWatermark = !string.IsNullOrWhiteSpace(step.Watermark);
        return step.Action switch
        {
            "setText" => ((hasName && step.Watermark == null) || (step.Name == null && hasWatermark)) && step.Text != null,
            "click" => hasName && step.Watermark == null && step.Text == null,
            "capture" => !string.IsNullOrWhiteSpace(step.Name) && step.Watermark == null && step.Text == null
                && step.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            _ => false,
        };
    }

    public static void Run(Window window, IReadOnlyList<ScreenshotStep> steps, Action settle, Action<string> capture)
    {
        foreach (var step in steps)
        {
            switch (step.Action)
            {
                case "setText":
                    FindControl<TextBox>(window, step).Text = step.Text;
                    settle();
                    break;
                case "click":
                    new ButtonAutomationPeer(FindControl<Button>(window, step)).Invoke();
                    settle();
                    break;
                case "capture":
                    capture(step.Name ?? throw new InvalidOperationException("Capture step requires a name."));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown screenshot action '{step.Action}'.");
            }
        }
    }

    static TControl FindControl<TControl>(Window window, ScreenshotStep step) where TControl : Control
    {
        var matches = window.GetVisualDescendants().OfType<TControl>()
            .Where(control => step.Name != null
                ? control.Name == step.Name
                : control is TextBox textBox && textBox.Watermark == step.Watermark)
            .ToArray();
        if (matches.Length != 1)
        {
            var selector = step.Name != null ? $"name '{step.Name}'" : $"watermark '{step.Watermark}'";
            throw new InvalidOperationException($"Screenshot {step.Action}: expected one {typeof(TControl).Name} with {selector}, found {matches.Length}.");
        }
        return matches[0];
    }
}
