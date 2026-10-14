FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /app

RUN case "$TARGETARCH" in \
      amd64) echo x64 > /tmp/dotnet-arch ;; \
      arm64) echo arm64 > /tmp/dotnet-arch ;; \
      *) echo "Arquitetura não suportada: $TARGETARCH" >&2; exit 1 ;; \
    esac

COPY Directory.Build.props Directory.Packages.props FCG.Notifications.slnx ./
COPY src/FCG.Notifications.Api/FCG.Notifications.Api.csproj src/FCG.Notifications.Api/
COPY src/FCG.Notifications.Application/FCG.Notifications.Application.csproj src/FCG.Notifications.Application/
COPY src/FCG.Notifications.Domain/FCG.Notifications.Domain.csproj src/FCG.Notifications.Domain/
COPY src/FCG.Notifications.Infrastructure/FCG.Notifications.Infrastructure.csproj src/FCG.Notifications.Infrastructure/

RUN dotnet restore src/FCG.Notifications.Api/FCG.Notifications.Api.csproj -a "$(cat /tmp/dotnet-arch)"

COPY . .

WORKDIR /app/src/FCG.Notifications.Api
RUN dotnet publish -c Release -a "$(cat /tmp/dotnet-arch)" --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "FCG.Notifications.Api.dll"]