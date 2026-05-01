FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Fizla.Domain/Fizla.Domain.csproj          src/Fizla.Domain/
COPY src/Fizla.Application/Fizla.Application.csproj    src/Fizla.Application/
COPY src/Fizla.Infrastructure/Fizla.Infrastructure.csproj src/Fizla.Infrastructure/
COPY src/Fizla.Api/Fizla.Api.csproj                src/Fizla.Api/
RUN dotnet restore src/Fizla.Api/Fizla.Api.csproj

COPY src/ src/
RUN dotnet publish src/Fizla.Api/Fizla.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN adduser --disabled-password --gecos "" --uid 1000 fizla \
    && chown -R fizla:fizla /app
USER fizla

COPY --from=build --chown=fizla:fizla /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

ENTRYPOINT ["dotnet", "Fizla.Api.dll"]
