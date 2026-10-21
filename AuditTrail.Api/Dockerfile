# Build context = repository root (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AuditTrail.Api/AuditTrail.Api.csproj AuditTrail.Api/
RUN dotnet restore AuditTrail.Api/AuditTrail.Api.csproj
COPY AuditTrail.Api/ AuditTrail.Api/
RUN dotnet publish AuditTrail.Api/AuditTrail.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Export files live on a mounted volume; the image runs as the non-root "app" user, so it must own the folder.
USER root
RUN mkdir -p /data/exports && chown -R $APP_UID /data/exports
USER $APP_UID
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuditTrail.Api.dll"]
