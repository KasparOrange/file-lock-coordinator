using System.Text.Json.Serialization;

namespace FileLockCoordinator;

[JsonSerializable(typeof(LockRequest))]
[JsonSerializable(typeof(UnlockRequest))]
[JsonSerializable(typeof(UnlockAllRequest))]
[JsonSerializable(typeof(LockResponse))]
[JsonSerializable(typeof(UnlockResponse))]
[JsonSerializable(typeof(UnlockAllResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(LockInfo))]
[JsonSerializable(typeof(IReadOnlyList<LockInfo>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class AppJsonContext : JsonSerializerContext { }
