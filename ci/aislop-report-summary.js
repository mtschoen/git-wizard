// Prints the analyzer execution records, missing tools, engine failures and finding counts of an aislop JSON report.
// With `--exit-reason <failBelow>` it instead prints one line naming why a nonzero exit left the score at or above failBelow.
const writeLine = (line) => process.stdout.write(`${line}
`);
const report = JSON.parse(require("fs").readFileSync(process.env.REPORT, "utf8"));
const engines = report.engines ?? {};
const missingTools = [
    ...new Set([...(report.missingTools ?? []), ...Object.values(engines).flatMap((engine) => engine.missingTools ?? [])]),
];
const engineFailures = Object.entries(engines).flatMap(([name, engine]) =>
    (engine.failures ?? []).map(
        (failure) => `${name} ${failure.tool ?? "unknown"} ${failure.target ?? "unknown"}: ${failure.reason ?? "no reason"}`,
    ),
);
const exitReasonIndex = process.argv.indexOf("--exit-reason");
if (exitReasonIndex !== -1) {
    const failBelow = Number(process.argv[exitReasonIndex + 1]);
    const reasons = [];
    if (missingTools.length > 0) reasons.push(`missing tools: ${missingTools.join(", ")}`);
    if (engineFailures.length > 0) reasons.push(`engine failures: ${engineFailures.join("; ")}`);
    const failedEngines = Object.entries(engines).filter(([, engine]) => engine.failed).map(([name]) => name);
    if (failedEngines.length > 0) reasons.push(`failed engines: ${failedEngines.join(", ")}`);
    if (report.score >= failBelow) {
        writeLine(
            `aislop gate: exited nonzero at score ${report.score} (failBelow ${failBelow}); ` +
                `${reasons.join("; ") || "no missing tool or engine failure in the report, inspect it"}`,
        );
    }
    process.exit(0);
}
for (const [name, engine] of Object.entries(engines)) {
    const seconds = ((engine.elapsed ?? 0) / 1000).toFixed(1);
    const state = engine.skipped ? "skipped" : "ran";
    writeLine(`aislop engine ${name}: ${state}, ${engine.issues ?? 0} issues, ${seconds}s`);
    for (const execution of engine.executions ?? []) {
        const analyzed = execution.analyzedProjects ?? [];
        const targets = (execution.targets ?? []).join(", ") || "none";
        const reason = execution.reason ? `, reason: ${execution.reason}` : "";
        writeLine(
            `aislop analyzer ${execution.tool}: ${execution.status}${reason}, ` +
                `${((execution.elapsed ?? 0) / 1000).toFixed(1)}s, targets: ${targets}, ` +
                `${analyzed.length} analyzed projects, ${(execution.excludedProjects ?? []).length} excluded`,
        );
        for (const project of analyzed) writeLine(`aislop analyzer ${execution.tool} analyzed: ${project}`);
    }
}
writeLine(`aislop missing tools: ${missingTools.join(", ") || "none"}`);
for (const failure of engineFailures) writeLine(`aislop engine failure: ${failure}`);
const counts = new Map();
for (const diagnostic of report.diagnostics ?? []) {
    const key = `${diagnostic.engine ?? "unknown"}/${diagnostic.severity ?? "unknown"}`;
    counts.set(key, (counts.get(key) ?? 0) + 1);
}
const text = [...counts].map(([key, count]) => `${key}=${count}`).join(" ");
writeLine(`aislop findings by engine/severity: ${text || "none"}`);
