using System.Text.Json.Serialization;

namespace AuditTrail.Api.Models;

// Enums are serialized as strings in JSON ("Security", not 0) and stored as text in PostgreSQL.

[JsonConverter(typeof(JsonStringEnumConverter<Category>))]
public enum Category { Security, Access, DataChange, Configuration, Financial }

[JsonConverter(typeof(JsonStringEnumConverter<Outcome>))]
public enum Outcome { Success, Failure, Denied }

[JsonConverter(typeof(JsonStringEnumConverter<ActorType>))]
public enum ActorType { User, Service, System }

[JsonConverter(typeof(JsonStringEnumConverter<ExportStatus>))]
public enum ExportStatus { Queued, Running, Completed, Failed, Cancelled, Expired }

[JsonConverter(typeof(JsonStringEnumConverter<ExportFormat>))]
public enum ExportFormat { Ndjson }

[JsonConverter(typeof(JsonStringEnumConverter<HoldScopeType>))]
public enum HoldScopeType { Actor, Resource, Category }

[JsonConverter(typeof(JsonStringEnumConverter<IngestStatus>))]
public enum IngestStatus { Accepted, Duplicate, Conflict, Rejected }

public static class Roles
{
    public const string Auditor = "Auditor";
    public const string ComplianceAdmin = "ComplianceAdmin";
}
