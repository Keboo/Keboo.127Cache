if (Environment.GetEnvironmentVariable("K127_FAKE_GH_PIN") is { } pin)
{
    var path = args.Length > 1 ? args[1] : "";
    if (pin != "match" && path.EndsWith("/actions/runs/100", StringComparison.Ordinal)
        && Environment.GetEnvironmentVariable("K127_FAKE_GH_RETRY_GATE") is { } retryGate)
    {
        var first = Path.Combine(retryGate, "first-lookup");
        if (File.Exists(first))
        {
            File.WriteAllText(Path.Combine(retryGate, "retry-started"), "");
            while (!File.Exists(Path.Combine(retryGate, "release-retry")))
                await Task.Delay(20);
        }
        else
            File.WriteAllText(first, "");
    }
    var gate = Environment.GetEnvironmentVariable("K127_FAKE_GH_RETRY_GATE");
    var corrected = gate is not null && File.Exists(Path.Combine(gate, "fixed-workflow"));
    var response = path switch
    {
        var value when value.Contains("/pulls?", StringComparison.Ordinal) => """[{"number":1}]""",
        var value when value.Contains("/actions/workflows/build.yml/runs?", StringComparison.Ordinal) => """{"workflow_runs":[]}""",
        var value when value.EndsWith("/actions/workflows/build.yml", StringComparison.Ordinal) => """{"id":123}""",
        var value when value.EndsWith("/actions/runs/100", StringComparison.Ordinal) =>
            $$"""{"id":{{(!corrected && pin == "wrong-id" ? 101 : 100)}},"run_attempt":1,"head_sha":"head","status":"completed","conclusion":"success","event":"pull_request","pull_requests":[{"number":1}],"workflow_id":{{(!corrected && pin == "missing-workflow" ? "null" : !corrected && pin == "mismatch" ? "456" : "123")}}}""",
        var value when value.Contains("/actions/runs/100/artifacts?", StringComparison.Ordinal) =>
            """{"artifacts":[{"id":1001,"name":"build-version","expired":false},{"id":1002,"name":"packages","expired":false}]}""",
        _ => null
    };
    if (response is not null)
    {
        Console.Write(response);
        return 0;
    }
}
Console.Error.WriteLine("Authentication failed. Run gh auth login. synthetic-sensitive-marker");
return 4;
