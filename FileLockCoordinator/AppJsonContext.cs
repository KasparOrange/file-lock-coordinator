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
[JsonSerializable(typeof(LocksResponse))]
[JsonSerializable(typeof(LockInfo))]
[JsonSerializable(typeof(IReadOnlyList<LockInfo>))]
[JsonSerializable(typeof(QueueResponse))]
[JsonSerializable(typeof(QueuesResponse))]
[JsonSerializable(typeof(QueueStatusDto))]
[JsonSerializable(typeof(IReadOnlyList<QueueStatusDto>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppJsonContext : JsonSerializerContext { }
