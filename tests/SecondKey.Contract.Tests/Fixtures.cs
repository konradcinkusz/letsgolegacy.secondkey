using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Runs;
using SecondKey.Contract.Observations;

namespace SecondKey.Contract.Tests;

/// <summary>Builds observations and results for tests, and finds the repository's samples.</summary>
internal static class Fixtures
{
    public static string Root { get; } = FindRoot();

    public static string Sample(string file) => Path.Combine(Root, "schemas", "samples", file);

    public static LoadedContract SampleContract() => ContractFile.Load(Sample("contract.yaml"));

    public static RunDocument SampleRun() => RunFile.Read(Sample("sample.skrun"));

    public static HttpRequestRecord Request(string method = "GET", string path = "/", string? query = null, BodyRecord? body = null, Dictionary<string, string[]>? headers = null) =>
        new() { Method = method, Path = path, Query = query, Headers = headers ?? new Dictionary<string, string[]>(), Body = body };

    public static HttpResponseRecord JsonResponse(int status, string json, Dictionary<string, string[]>? headers = null) =>
        new()
        {
            Status = status,
            Headers = headers ?? new Dictionary<string, string[]> { ["content-type"] = ["application/json"] },
            Body = BodyRecord.FromJson(JsonNode.Parse(json), "application/json"),
        };

    public static HttpResponseRecord HtmlResponse(int status, string html) =>
        new()
        {
            Status = status,
            Headers = new Dictionary<string, string[]> { ["content-type"] = ["text/html; charset=utf-8"] },
            Body = BodyRecord.FromText(html, "text/html"),
        };

    public static Observation Observe(HttpRequestRecord request, HttpResponseRecord? response, ExchangeError? error = null, ContractDocument? contract = null) =>
        new ObservationBuilder(new Extraction.ExtractorSet(contract?.Extract)).Build(request, response, error, db: null, outbound: null);

    public static PredicateDefinition Predicate(string yaml)
    {
        var node = Artifacts.Yaml.YamlJson.Convert(yaml)!;
        return node.Deserialize<PredicateDefinition>(ArtifactJson.Document)!;
    }

    public static ContractDocument ContractOf(string yaml) => ContractFile.LoadText(yaml, "test").Document;

    /// <summary>A copy of the sample run with the candidate's answer to one exchange replaced.</summary>
    public static RunDocument WithCandidate(RunDocument run, string exchange, Func<ExchangeResult, ExchangeResult> change) =>
        run with
        {
            Results = run.Results.Select(r => r.Side == Side.Candidate && r.Exchange == exchange ? change(r) : r).ToList(),
        };

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SecondKey.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SecondKey.slnx not found");
    }
}
