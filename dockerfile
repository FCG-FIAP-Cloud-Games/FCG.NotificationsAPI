# Estágio de Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copia os arquivos de configuração e projetos
COPY Directory.Build.props Directory.Packages.props FCG.Notifications.slnx ./
COPY src/FCG.Notifications.Api/FCG.Notifications.Api.csproj src/FCG.Notifications.Api/
COPY src/FCG.Notifications.Application/FCG.Notifications.Application.csproj src/FCG.Notifications.Application/
COPY src/FCG.Notifications.Domain/FCG.Notifications.Domain.csproj src/FCG.Notifications.Domain/
COPY src/FCG.Notifications.Infrastructure/FCG.Notifications.Infrastructure.csproj src/FCG.Notifications.Infrastructure/

# Restaura as dependências usando o csproj da API
RUN dotnet restore src/FCG.Notifications.Api/FCG.Notifications.Api.csproj

# Copia o restante do código fonte
COPY . .

# Publica a aplicação
WORKDIR /app/src/FCG.Notifications.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Estágio de Execução (Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FCG.Notifications.Api.dll"]