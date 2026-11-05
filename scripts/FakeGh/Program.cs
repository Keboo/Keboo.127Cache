var log = Environment.GetEnvironmentVariable("KEBOO_CI_GH_LOG");
if (string.IsNullOrWhiteSpace(log) || args is not ["api", var request])
{
    Console.Error.WriteLine("The CI fixture only accepts configured gh api requests.");
    return 1;
}

var response = request switch
{
    "repos/ci-fixture/empty/pulls?state=open&per_page=100&page=1" => """[{"number":1}]""",
    "repos/ci-fixture/empty/actions/workflows/ci.yml/runs?event=pull_request&status=success&per_page=100&page=1"
        => """{"workflow_runs":[]}""",
    _ => null
};
if (response is null)
{
    Console.Error.WriteLine($"Unexpected CI fixture request: {request}");
    return 1;
}

File.AppendAllText(log, request + Environment.NewLine);
Console.WriteLine(response);
return 0;
