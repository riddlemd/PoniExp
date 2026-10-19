# Build from the repo root: docker build -f src/PoniExp.Router/Dockerfile -t poniexp-router .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PoniExp.Core/PoniExp.Core.csproj src/PoniExp.Core/
COPY src/PoniExp.Router/PoniExp.Router.csproj src/PoniExp.Router/
RUN dotnet restore src/PoniExp.Router/PoniExp.Router.csproj
COPY src/PoniExp.Core/ src/PoniExp.Core/
COPY src/PoniExp.Router/ src/PoniExp.Router/
RUN dotnet publish src/PoniExp.Router/PoniExp.Router.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# `docker exec poniexp-router poniexp-router admin list`
RUN printf '#!/bin/sh\nexec dotnet /app/PoniExp.Router.dll "$@"\n' > /usr/local/bin/poniexp-router \
    && chmod 755 /usr/local/bin/poniexp-router \
    && mkdir -p /data && chown app:app /data
USER app
ENV PoniExp__DataDirectory=/data \
    PoniExp__Port=5443 \
    DOTNET_gcServer=0
VOLUME /data
EXPOSE 5443
ENTRYPOINT ["dotnet", "/app/PoniExp.Router.dll"]
