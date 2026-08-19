# YARP UI (community edition) — multi-stage build.
# Build context: the folder containing this file (the solution root), e.g.
#   docker build -t yarp-ui:0.1.0 .
# or simply use docker-compose.yml.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached unless the project files change.
# The host pulls in the YARPUI library via a project reference.
COPY YARPUI/YARPASUI.csproj YARPUI/
COPY YARPUI.Host/YARPUI.Host.csproj YARPUI.Host/
RUN dotnet restore YARPUI.Host/YARPUI.Host.csproj

COPY YARPUI/ YARPUI/
COPY YARPUI.Host/ YARPUI.Host/
RUN dotnet publish YARPUI.Host/YARPUI.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Mutable configuration (appsettings.json override + yarp-ui.routes.json) lives in
# /app/data — mount a volume there to persist it across container recreation.
ENV YarpUi__DataDirectory=/app/data

# Create the data dir as app-owned so fresh named volumes inherit that ownership
# (root-owned otherwise, which breaks writes at runtime for the non-root user).
RUN mkdir -p /app/data && chown app:app /app/data

# aspnet:10.0 listens on 8080 by default (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "YARPUI.Host.dll"]
