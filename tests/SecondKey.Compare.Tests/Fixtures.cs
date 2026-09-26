using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using SecondKey.Contract.Extraction;
using SecondKey.Contract.Observations;

namespace SecondKey.Compare.Tests;

/// <summary>Builds contracts, observations and runs for tests, and finds the repository's samples.</summary>
internal static class Fixtures
{
    public const string LegacyUrl = "http://legacy.test:5080";
    public const string CandidateUrl = "http://candidate.test:5081";

    /// <summary>The smallest valid contract: one accepted "never" clause that no test trips over.</summary>
    public const string MinimalClauses = """
        clauses:
          - id: NEVER-TEAPOT
            kind: never
            title: Never a teapot
            why: A teapot is not a shop.
            assert:
              - { select: response.status, op: equals, value: 418 }
            provenance: { origin: designed, status: accepted }
        """;

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Root { get; } = FindRoot();

    /// <summary>Compact JSON with "&lt;" and "é" left readable, as artifacts write them.</summary>
    public static string Text(JsonNode? node) => node?.ToJsonString(Readable) ?? "null";

    public static string Sample(string file) => Path.Combine(Root, "schemas", "samples", file);

    public static LoadedContract SampleContract() => ContractFile.Load(Sample("contract.yaml"));

    public static RunDocument SampleRun() => RunFile.Read(Sample("sample.skrun"));

    public static ContractDocument ContractOf(string yaml) => ContractFile.LoadText(yaml, "test").Document;

    /// <summary>A contract with the given sections before <see cref="MinimalClauses"/>, and further clause items after it.</summary>
    public static ContractDocument ContractWith(string sections = "", string extraClauses = "") => ContractOf($"""
        apiVersion: secondkey/v1
        kind: Contract
        metadata:
          name: test
          revision: 1
        {sections}
        {MinimalClauses}
        {extraClauses}
        """);

    public static Normalizer NormalizerOf(string normalizeYaml) => new(ContractWith(normalizeYaml).Normalize);

    public static HttpRequestRecord Request(string method = "GET", string path = "/", string? query = null) =>
        new() { Method = method, Path = path, Query = query, Headers = new Dictionary<string, string[]>() };

    public static HttpResponseRecord JsonResponse(int status, string json, Dictionary<string, string[]>? headers = null) =>
        new()
        {
            Status = status,
            Headers = headers ?? new Dictionary<string, string[]> { ["content-type"] = ["application/json; charset=utf-8"] },
            Body = BodyRecord.FromJson(JsonNode.Parse(json), "application/json"),
        };

    public static HttpResponseRecord TextResponse(int status, string text, string contentType = "text/plain; charset=utf-8") =>
        new()
        {
            Status = status,
            Headers = new Dictionary<string, string[]> { ["content-type"] = [contentType] },
            Body = BodyRecord.FromText(text, contentType),
        };

    public static Observation Observe(HttpResponseRecord? response, ContractDocument? contract = null, ExchangeError? error = null, HttpRequestRecord? request = null, DbDelta? db = null) =>
        new ObservationBuilder(new ExtractorSet(contract?.Extract)).Build(request ?? Request(), response, error, db, outbound: null);

    public static ExchangeResult Result(string exchange, Side side, HttpResponseRecord? response, HttpRequestRecord? request = null, ExchangeError? error = null, string scenario = "s-1") =>
        new()
        {
            Exchange = exchange,
            Scenario = scenario,
            Side = side,
            Seq = 1,
            StartedAt = DateTimeOffset.UnixEpoch,
            DurationMs = 1,
            Request = request ?? Request(),
            Response = response,
            Error = error,
        };

    /// <summary>A pair of results for one exchange: what legacy answered and what the candidate answered.</summary>
    public static IEnumerable<ExchangeResult> Pair(string exchange, HttpResponseRecord? legacy, HttpResponseRecord? candidate, HttpRequestRecord? request = null, string scenario = "s-1") =>
    [
        Result(exchange, Side.Legacy, legacy, request, legacy is null ? new ExchangeError { Kind = "timeout", Message = "no answer" } : null, scenario),
        Result(exchange, Side.Candidate, candidate, request, candidate is null ? new ExchangeError { Kind = "connection", Message = "refused" } : null, scenario),
    ];

    public static RunDocument RunOf(params IEnumerable<ExchangeResult> results)
    {
        var list = results.ToList();
        var header = new RunHeader
        {
            RunId = "run-test",
            StartedAt = DateTimeOffset.UnixEpoch,
            Tool = ToolInfo.Current,
            Capture = new CaptureReference { CaptureId = "cap-test", Sha256 = new string('0', 64) },
            Sides = new RunSides { Legacy = new SideInfo { BaseUrl = LegacyUrl + "/" }, Candidate = new SideInfo { BaseUrl = CandidateUrl + "/" } },
            ScenarioMode = ScenarioMode.Session,
        };
        return new RunDocument(header, [], list, new RunEnd { FinishedAt = DateTimeOffset.UnixEpoch, Scenarios = 1, Results = list.Count });
    }

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
