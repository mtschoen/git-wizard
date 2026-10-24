// Prints the analyzer execution records and finding counts of an aislop JSON report.
const writeLine = (line) => process.stdout.write(`${line}
`);
const report = JSON.parse(require("fs").readFileSync(process.env.REPORT, "utf8"));
const engines = report.engines ?? {};
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
const counts = new Map();
for (const diagnostic of report.diagnostics ?? []) {
    const key = `${diagnostic.engine ?? "unknown"}/${diagnostic.severity ?? "unknown"}`;
    counts.set(key, (counts.get(key) ?? 0) + 1);
}
const text = [...counts].map(([key, count]) => `${key}=${count}`).join(" ");
writeLine(`aislop findings by engine/severity: ${text || "none"}`);
