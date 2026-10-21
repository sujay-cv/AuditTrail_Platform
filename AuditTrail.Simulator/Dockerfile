FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AuditTrail.Simulator/ AuditTrail.Simulator/
RUN dotnet publish AuditTrail.Simulator/AuditTrail.Simulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "AuditTrail.Simulator.dll"]
