using System.Text.Json.Serialization;

namespace GitWizard;

public sealed record ReportRunSummary
{
    [JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Created { get; init; }

    [JsonPropertyName("refreshed"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Refreshed { get; init; }

    [JsonPropertyName("skipped"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Skipped { get; init; }

    [JsonPropertyName("warnings")]
    public string[] Warnings { get; init; } = [];
}
