# The station API. Build context is the repo root, not this directory -- Mcs.Api references
# Mcs.Core and Mcs.Adapters, and a context rooted here could not see them:
#
#     docker build -f src/Mcs.Api/Dockerfile -t mcs-api .
#
# Tags are pinned to exact versions rather than :10.0, so a rebuild months from now is the
# same build. Digests would make that airtight and were rejected: every base-image security
# update becomes a manual edit, which is a maintenance cost this project cannot yet carry.
# The SDK tag matches global.json's 10.0.302 -- an image on an older feature band fails with
# a global.json error that reads like a Docker problem and is not one.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src

#  Project files and the property sheets that shape restore, ahead of any source. A source
#  edit then rebuilds without re-resolving NuGet -- the difference between a 15-second and a
#  90-second rebuild, on every CI run from here on.
#
#  Restoring the one project rather than the solution keeps the four test projects out of the
#  image entirely: they are not shipped, and their .csproj files would be four more COPY
#  lines whose only effect is to invalidate this cache more often.
COPY global.json Directory.Build.props ./
COPY src/Mcs.Core/Mcs.Core.csproj src/Mcs.Core/
COPY src/Mcs.Adapters/Mcs.Adapters.csproj src/Mcs.Adapters/
COPY src/Mcs.Api/Mcs.Api.csproj src/Mcs.Api/
RUN dotnet restore src/Mcs.Api/Mcs.Api.csproj

#  Not shipped as loose files -- Mcs.Api.csproj compiles these in as embedded resources, so
#  the runtime stage needs nothing from here. They are copied because the glob has to match
#  something at build time: if it matches nothing the build still succeeds, and the image
#  starts cleanly against a database it has not migrated. Ahead of the source because
#  migrations change far less often than code does.
COPY deploy/migrations/ deploy/migrations/
COPY src/ src/

#  Release, not Debug. TreatWarningsAsErrors and the NuGetAudit settings in
#  Directory.Build.props apply here, so the image build re-proves the clean-build property
#  rather than trusting that somebody's laptop did.
RUN dotnet publish src/Mcs.Api/Mcs.Api.csproj -c Release --no-restore -o /app


FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

#  Set explicitly although this base image already does. Kestrel's own default binds
#  localhost, which inside a container means every connection from outside it is refused by a
#  process that started and logged nothing wrong -- a confusing twenty minutes, bought off
#  with one line that also documents the port the Compose healthcheck and nginx must agree on.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

#  No HTTPS inside the container, and no dev-certificate dance: nginx terminates in front of
#  this. Program.cs leaves UseHttpsRedirection out for the same reason -- with it in, every
#  proxied request 307s to an https port nothing is listening on.

#  This base image ships no HTTP client at all -- no curl, no wget -- so without this line a
#  container healthcheck has nothing to call /health with. The alternative was to keep the
#  image untouched and drive a bash /dev/tcp probe from Compose, which was rejected twice
#  over: it proves a socket accepts rather than that the endpoint answers, and it puts a line
#  nobody can read at a glance in the first file a reviewer opens. Above USER because apt
#  needs root; the lists are removed in the same layer or they ship inside the image.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

#  APP_UID comes from the base image. Its absence is a standard review finding and the fix is
#  this one line, so there is no version of this file where it is worth omitting.
USER $APP_UID

#  No HEALTHCHECK here. It belongs in Compose, where the probe can be changed without
#  rebuilding the image, and where the image stays agnostic about who is watching it.
ENTRYPOINT ["dotnet", "Mcs.Api.dll"]
