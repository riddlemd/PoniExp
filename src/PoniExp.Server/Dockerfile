# Build from the repo root: docker build -f src/PoniExp.Server/Dockerfile -t poniexp-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PoniExp.Core/PoniExp.Core.csproj src/PoniExp.Core/
COPY src/PoniExp.Server/PoniExp.Server.csproj src/PoniExp.Server/
RUN dotnet restore src/PoniExp.Server/PoniExp.Server.csproj
COPY src/PoniExp.Core/ src/PoniExp.Core/
COPY src/PoniExp.Server/ src/PoniExp.Server/
RUN dotnet publish src/PoniExp.Server/PoniExp.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# `docker exec poniexp-server poniexp admin list`
RUN printf '#!/bin/sh\nexec dotnet /app/poniexp.dll "$@"\n' > /usr/local/bin/poniexp \
    && chmod 755 /usr/local/bin/poniexp \
    && mkdir -p /data && chown app:app /data
USER app
ENV PoniExp__DataDirectory=/data \
    PoniExp__Port=5443 \
    DOTNET_gcServer=0
VOLUME /data
EXPOSE 5443
ENTRYPOINT ["dotnet", "/app/poniexp.dll", "router"]
