using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AuditTrail.Api.Infrastructure;

/// <summary>Small tweaks to the generated OpenAPI document.</summary>
public static class OpenApiSetup
{
    /// <summary>Adds the auth headers so you can call the API from Swagger UI ("Authorize" button).</summary>
    public static Task AddHeaderAuth(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "AuditTrail API";
        document.Info.Description = "Multi-tenant, tamper-evident audit trail. Producers use X-Api-Key; the console uses X-Tenant-Id + X-Role.";

        var schemes = new Dictionary<string, string>
        {
            ["X-Api-Key"] = "Producer API key, e.g. acme-payments-key (demo)",
            ["X-Tenant-Id"] = "Tenant id, e.g. 11111111-1111-1111-1111-111111111111 (demo)",
            ["X-Role"] = "Auditor or ComplianceAdmin",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        foreach (var (header, description) in schemes)
        {
            document.Components.SecuritySchemes[header] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = header,
                Description = description,
            };
        }

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("X-Api-Key", document)] = [] });
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("X-Tenant-Id", document)] = [],
            [new OpenApiSecuritySchemeReference("X-Role", document)] = [],
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks every non-nullable C# property as "required" in the schema, so the generated TypeScript types
    /// say `sequence: number` instead of `sequence?: number`.
    /// </summary>
    public static Task MarkNonNullablePropertiesRequired(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        if (context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object || schema.Properties is null) return Task.CompletedTask;

        schema.Required ??= new HashSet<string>();
        foreach (var property in context.JsonTypeInfo.Properties)
        {
            if (!property.IsGetNullable && schema.Properties.ContainsKey(property.Name))
                schema.Required.Add(property.Name);
        }
        return Task.CompletedTask;
    }
}
