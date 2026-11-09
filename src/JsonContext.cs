// JsonContext.cs - compile-time JSON for the snapshot and the log stream (NativeAOT has no reflection-based JSON).
// Copyright (c) 2026 Jared Brown. MIT licence.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FSPerf;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SettingsView))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(List<RelayMember>))]
[JsonSerializable(typeof(GroupMemberView))]
internal partial class FsperfJson : JsonSerializerContext
{
    // log lines: the page shows them as text, so the relaxed encoder (no < for '<' etc.) keeps them readable
    // Lazy: a static field initializer here would run before the generated half of this partial class sets Default
    // (initializer order across partial files is undefined; NativeAOT crashed on it with a NullReferenceException)
    private static FsperfJson? _relaxed;

    public static JsonTypeInfo<string> LogText => Relaxed.String;
    // the snapshot a sharer sends (#14): non-ASCII text stays readable so the redaction can match it before it leaves
    public static JsonTypeInfo<Snapshot> RelaxedSnapshot => Relaxed.Snapshot;

    private static FsperfJson Relaxed => _relaxed ??= new FsperfJson(new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
}
