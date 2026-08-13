FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src

#  csproj files first, so a source-only change reuses the restore layer. Mcs.Core comes along
#  because Mcs.Adapters references it -- the simulator itself names neither the store nor the
#  telemetry model, and must not start.
COPY global.json Directory.Build.props ./
COPY src/Mcs.Core/Mcs.Core.csproj src/Mcs.Core/
COPY src/Mcs.Adapters/Mcs.Adapters.csproj src/Mcs.Adapters/
COPY src/Mcs.Simulator/Mcs.Simulator.csproj src/Mcs.Simulator/
RUN dotnet restore src/Mcs.Simulator/Mcs.Simulator.csproj

COPY src/ src/

RUN dotnet publish src/Mcs.Simulator/Mcs.Simulator.csproj -c Release --no-restore -o /app


#  runtime, not aspnet: this process listens on nothing. It opens an outbound UDP socket and
#  talks, so the web stack the API's image carries would be weight with no caller.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

#  No EXPOSE and no published port anywhere. The asymmetry is the point: a vehicle transmits
#  to a ground station, so only the station binds.
#
#  No curl either, which the API's image installs for its healthcheck. This service has none:
#  there is nothing to ask it, and `docker compose up --wait` is satisfied by a container that
#  is running. A simulator that has stopped is a simulator that failed to start, and it exits
#  non-zero so that says itself.

USER $APP_UID

ENTRYPOINT ["dotnet", "Mcs.Simulator.dll"]
